# Signed audit exports

Implemented locally: tenant-bound JSONL export, HMAC-SHA256 signed manifest, ZIP download for auditors/supervisors/admins, exclusive local output, an offline verifier, role/CSRF/tenant tests, and alteration/truncation/deletion/reordering/insertion/key-mismatch tests. The signing key comes from the existing bounded environment/mounted-file secret provider. No cloud storage API is implemented or called.

The export includes the municipality's case/system audit chain, including training and administration events mirrored into that chain. It does not export raw service-account credentials, resident-contact stores, case snapshots, or every underlying store's separate state chain. It proves the included chain and signed checkpoint are consistent; it does not prove that an administrator never omitted older data before this checkpoint or that case snapshots can be restored from the export. Use [backup/restore](BACKUP_RESTORE.md) for recovery.

## Configuration and access

Set `CIVICOPS_AUDIT_SIGNING_KEY` or its absolute mounted-file counterpart `CIVICOPS_AUDIT_SIGNING_KEY_FILE`. The key must contain at least 32 UTF-8 bytes, must meet the existing secret-provider bounds, and should be generated with a cryptographic random generator by the owner. Never commit it, pass it as a command-line argument, or include it in the export. Use `CIVICOPS_AUDIT_SIGNING_KEY_ID` to identify the historical verification key (1–64 ASCII letters/digits/hyphens/underscores; default `owner-key`). Archive historical keys separately under restricted access after rotation.

Signed export is **disabled with HTTP 503 when a key is absent**, including the zero-configuration synthetic demo. No public/demo signing key is embedded. An invalid configured secret source fails closed without showing values or paths. `CIVICOPS_AUDIT_EXPORT_DIR` selects an absolute local target; when absent, signed download still works but local output is disabled. Every setting is blank in the example environment inventories.

- `GET /api/audit/export` downloads a ZIP containing exactly `audit.jsonl` and `manifest.json`.
- `POST /api/audit/export/local` creates a fresh unpredictable `audit-<UUID>` directory beneath the configured target and returns the manifest plus its export ID. Cookie authentication requires the existing CSRF token. The tenant comes from the authenticated municipality binding; no request field selects another municipality.
- Auditors, supervisors and administrators may export. Other roles are denied; scoped service accounts additionally need `audit:read`.

Historical audit details can contain historical PII because changing a hashed field would break its chain. Treat exported artifacts as sensitive municipal records, restrict recipients, encrypt storage/transit, and include them in the owner's POPIA retention/access policy. The application never logs the export contents or signing key.

## Local CLI and offline verification

Create an export from a stopped installation using the normal storage settings and content root. The output must be a new absolute directory:

```bash
dotnet run --project products/civicops -c Release -- audit export "$TMPDIR/municipal-audit-export" --tenant default
```

The default municipality is `default`; `--tenant` must name an existing municipality. Do not run the file-store export CLI against a data directory owned by another running CivicOps process. For a running installation, use the protected HTTP endpoints. CLI startup uses the selected backend and its normal integrity checks; it does not contact live providers.

Extract the downloaded ZIP into a fresh directory, provide the historical signing key using the secret provider, then verify with no server or database:

```bash
./scripts/audit_verify.sh "$TMPDIR/municipal-audit-export"
# Equivalent published-app command:
dotnet products/civicops/bin/Release/net10.0/CivicOps.dll audit verify "$TMPDIR/municipal-audit-export"
```

Exit codes: `0` verified; `1` integrity failure; `2` invalid usage, unreadable artifact, or unavailable key. Verification authenticates the signature, SHA-256 of the exact JSONL bytes, byte/entry counts, first/last sequence, chain head, every original audit hash/link, and tenant binding. It rejects duplicate JSON property names, unknown properties, symbolic-link paths, invalid/truncated JSONL, over-64-KiB lines/manifests, more than 100,000 entries, and JSONL over 64 MiB. Larger chains require a reviewed export-format extension rather than silently omitting entries. Signer and verifier enforce the same limits.

The manifest format is `CivicOpsAuditExport/v1`. SHA-256 and original audit hashes use uppercase hexadecimal. The signature is base64 HMAC-SHA256 over the UTF-8 encoding of these fields, separated by LF, with a final LF:

```text
format
exportId
tenantId
exportedAt (UTC .NET O format: yyyy-MM-ddTHH:mm:ss.fffffff+00:00)
entries (invariant decimal)
firstSequence (invariant decimal, empty for zero entries)
lastSequence (invariant decimal, empty for zero entries)
chainHead (GENESIS for zero entries)
sha256
byteLength (invariant decimal)
signatureAlgorithm (HMAC-SHA256)
signatureKeyId
```

JSON field order and whitespace do not affect the manifest signature. JSONL bytes, including their final LF, are authenticated by its SHA-256. HMAC is a shared-secret integrity mechanism: anyone holding the verification key can create a valid signature. It is not asymmetric non-repudiation or independent third-party attestation. Retain previous signed checkpoints outside the application and compare their head/count before accepting subsequent checkpoints; an isolated signature cannot establish an external trusted history.

## Local immutability and cloud targets

The implemented target uses a new UUID directory, exclusive `CreateNew` files, an exclusive reservation, flushed writes, a final `COMPLETE` marker, restricted/read-only files, and rejects symbolic links or replacement. It never appends to or overwrites an existing export. An interrupted write leaves an incomplete directory for the operator to inspect; do not reuse that directory. The downloaded ZIP needs no `COMPLETE` marker; the verifier validates its two artifacts independently. Host administrators can change permissions/delete local files, so this is application-enforced append-only behavior, **not verified WORM storage**. Directory fsync and protection against a hostile host administrator are not claimed.

S3 Object Lock and Azure immutable blob targets are **documented-only owner deployment options**. An operator can copy the complete artifacts into a separately controlled target using unique object names, record their storage version identifiers/retention evidence, and verify after retrieval. The application does not upload, apply retention policies, create accounts, or attest a cloud lock.

For S3, configure Versioning and Object Lock, set a reviewed retention period/legal hold, and verify the retained object version. Governance mode has a privileged bypass; compliance mode prevents modification/deletion for the applicable retention period. Separate uploader rights from retention administration and verification-key custody. See [S3 Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html) and [configuration](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-configure.html).

For Azure, select container/version immutability, validate in an unlocked policy, then lock the reviewed time-based retention policy or apply a legal hold. Upload complete JSONL/manifest objects with unique names; protected append writes are unnecessary for these finalized artifacts. The owner's retention policy must consider lawful preservation and privacy obligations. See [Azure immutable storage](https://learn.microsoft.com/azure/storage/blobs/immutable-time-based-retention-policy-overview) and [container policy configuration](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-policy-configure-container-scope).

Cloud IAM, separate trust domains, encryption, retention/legal holds, historical-key custody, external checkpoints, scheduled exports, and actual immutability/retrieval exercises remain owner-pending. Local verification evidence is `AuditExportTests` and `./scripts/smoke_audit_export.sh`; cloud behavior remains unverified.
