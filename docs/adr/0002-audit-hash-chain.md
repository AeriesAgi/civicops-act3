# 0002 — Hash-chained audit and authenticated exports

- Status: accepted; external anchoring and enforced immutable retention remain owner-pending.
- Date recorded: 2026-10-08 (SAST).
- Scope: tenant audit integrity and evidence custody.

## Context and Problem Statement

Dispatchers and reviewers must reconstruct who approved a case, what the agents recommended and which crew was assigned. Recording events alone cannot detect a modified history or a case snapshot changed after its last event. Evidence exports must also be verifiable after the application or source store becomes unavailable.

## Decision Drivers

- Detect changes, deletion, reordering and retained-state mismatch.
- Preserve historical events and tenant isolation across migrations.
- Keep verification possible offline with bounded, privacy-conscious artifacts.
- Avoid claiming trust that a storage administrator can defeat.

## Considered Options

- Mutable application logs as the only audit.
- An external ledger as a mandatory runtime dependency.
- Local hash chains plus separately authenticated export bundles and a future external anchor.

## Decision Outcome

Choose local chains and export bundles. [AuditLog.cs](../../products/civicops/Services/AuditLog.cs) hashes each entry's sequence, event fields, previous hash, tenant and serialized case-state hash with SHA-256. `Verify` recomputes the sequence and chain; `VerifySnapshot` compares each retained incident with its latest authenticated state. [IncidentStore.cs](../../products/civicops/Services/IncidentStore.cs) rejects changes to historical events and refuses mutation after integrity failure. Separately stored optional contacts follow their own authenticated privacy-store controls.

Workforce, training, catalogue and identity stores maintain their own immutable histories and recover missing projections into the tenant incident audit. [DurableStorage.cs](../../products/civicops/Services/DurableStorage.cs) commits PostgreSQL snapshots/projections together and rejects replacement of an existing audit sequence/hash or truncation of its projected tail.

[AuditExportService.cs](../../products/civicops/Services/AuditExportService.cs) produces JSONL and an HMAC-SHA256 manifest authenticating tenant, counts, chain head and exact file checksum. Export requires the audit policy and an owner-provided signing key; no key means the feature is honestly disabled. The local export target creates files exclusively and makes completed artifacts read-only. [audit_verify.sh](../../scripts/audit_verify.sh) verifies bundles offline.

## Consequences

Accidental or partial tampering fails closed and is reviewable. Export consumers need the matching protected HMAC key and key identifier. HMAC is shared-secret authentication, not a publicly verifiable asymmetric signature; a verifier holding that key can also create an authenticated bundle.

A host administrator or database superuser can rewrite a chain and its snapshots together. Local read-only file permissions are not host-enforced WORM. Neither hash chaining nor HMAC establishes an independent timestamp, legal nonrepudiation, complete privacy erasure or protection against privileged rewriting. External anchoring, cloud Object Lock/immutable retention, custody procedures and key rotation approval remain deployment work. Audit records and provider artifacts also have independent retention obligations.

## Confirmation

Implemented and tested locally: [PersistenceAuditTests.cs](../../tests/CivicOps.Tests/PersistenceAuditTests.cs) checks chain/snapshot damage; [PostgresStorageTests.cs](../../tests/CivicOps.Tests/PostgresStorageTests.cs) checks projection truncation and hash-preserving body rewrites. [AuditExportTests.cs](../../tests/CivicOps.Tests/AuditExportTests.cs) rejects modified, truncated, reordered or inserted JSONL, changed manifest claims and wrong keys. [smoke_audit_export.sh](../../scripts/smoke_audit_export.sh) exercises real local exports and offline verification.

[Audit export operations](../operations/AUDIT_EXPORT.md) describe signing-key custody and documented cloud options. External anchors and cloud retention have not been exercised.
