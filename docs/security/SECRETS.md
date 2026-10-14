# Secret sources and logging

Implemented and locally tested: `ISecretProvider`, the default environment/configuration source, mounted UTF-8 files, precedence, bounded files, fail-closed errors, provider option wiring, readiness omission, and redacted logging. No remote secret API was called.

`CIVICOPS_SECRET_PROVIDER` defaults to `env`; `files` is an equivalent explicit selection. For every credential below, an absolute `NAME_FILE` path takes precedence over `NAME`. Missing, unreadable, empty, invalid UTF-8, control-containing or over-16-KiB mounted values fail closed with a generic exception. A trailing newline from a Kubernetes secret is removed. Credential option factories read current files; existing OIDC handler settings, PostgreSQL data-source connections and already-issued sessions still require restart/re-authentication to refresh. Audit signing/verifying reads the current secret for each operation; retain historical verification keys separately after rotation. Mount versioned secrets atomically, use read-only volumes and restrict file permissions.

Supported names:

- `CIVICOPS_OIDC_CLIENT_SECRET`, `CIVICOPS_LLM_API_KEY`
- `ELEVENLABS_API_KEY`, `ELEVENLABS_WEBHOOK_SECRET`, `CIVICOPS_ELEVENLABS_TOOL_SECRET`
- `ZENDESK_API_TOKEN`, `ZENDESK_OAUTH_TOKEN`, `ZENDESK_WEBHOOK_SECRET`
- `CIVICOPS_EMAIL_SMTP_PASSWORD`, `CIVICOPS_DISPATCH_TOKEN`
- `CIVICOPS_DATABASE_URL`, `CIVICOPS_AUDIT_SIGNING_KEY`
- `CIVICOPS_DISPATCHERS`, `CIVICOPS_TRAINING_CLIENTS`, `CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH` (hashed credential lists)

All `.env.example` values remain blank. Do not put real credentials into repository files, CLI arguments, URLs, screenshots or synthetic fixtures. Kubernetes can set an environment path such as `ELEVENLABS_API_KEY_FILE` to a read-only secret-volume file. The actual value belongs in the external secret manager; manifests must contain only references. Environment and mounted secrets are process-memory plaintext and require host access controls.

PostgreSQL resolves `CIVICOPS_DATABASE_URL` / `_FILE` through this provider and fails startup when selected but unconfigured; it never silently falls back to files. A URL or connection string can contain database credentials, so logs, telemetry and health responses exclude it. Use a dedicated database, reviewed TLS settings and restricted grants; [storage configuration](../operations/STORAGE.md) and [backup/restore](../operations/BACKUP_RESTORE.md) describe the application/recovery boundaries.

`CIVICOPS_AUDIT_SIGNING_KEY` / `_FILE` supplies the HMAC-SHA256 export key and requires at least 32 UTF-8 bytes. An owner must generate a cryptographically random key; no shared demo signing key is supplied. Missing configuration disables signed export honestly. `CIVICOPS_AUDIT_SIGNING_KEY_ID` is an optional nonsecret historical-key label. Key custody, rotation and retained verification keys are owner responsibilities; anyone holding the HMAC key can create a valid signature. [Audit export](../operations/AUDIT_EXPORT.md) documents offline verification, exclusive local files and documented-only cloud WORM targets.

`RedactingLoggerProvider` wraps configured application/framework log providers, sanitises message formatters and scopes, and records only exception type names. `SensitiveValueRedactor` removes configured and previously observed credential literals and applies `PiiRedactor`. Rotation retains observed values in process memory so old credential strings remain redacted. Startup source failures contain neither values nor paths. Health/readiness responses expose configuration booleans and variable names, never secrets. New log sinks must be registered before `AddCivicOpsSecrets` or wrapped in the same provider; adapters must not add raw sinks later. Redaction is defence in depth: do not deliberately log credentials or resident text. SDK/network/host logs outside this process remain an operator responsibility.

Azure Key Vault and HashiCorp Vault adapters are **documented-only extension points**, with no SDK dependency or network implementation. Selecting `azure-key-vault`, `hashicorp-vault` or an unknown source fails closed; it never falls back silently. A future adapter must implement `ISecretProvider`, use the official SDK, validate fixed HTTPS authorities, use workload identity, bound reads/timeouts, redact failures, support rotation and pass the sentinel tests. Today use a secrets-store CSI driver or operator to mount values through the tested file source. Workload identity, RBAC, encryption, rotation schedules and provider connectivity are owner-pending.

Evidence: `SecretProvider.cs`, option factories, `SecretProviderTests`, and [privacy redaction policy](../privacy/POPIA.md). This is a local implementation review, not a secret-manager certification.
