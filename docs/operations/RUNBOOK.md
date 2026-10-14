# CivicOps municipal operations runbook

Reviewed **8 October 2026 (SAST, UTC+02:00)**. Application controls and recovery scripts are implemented and tested locally. Deployment manifests are statically validated; production deployment, monitoring receivers, real municipal SSO, AMD hardware and provider delivery are owner-pending. This runbook defines the operator procedure; it does not establish a managed service or measured production SLA. Use the [readiness matrix](../ENTERPRISE_READINESS.md) and [verification evidence](../VERIFICATION.md) when approving a pilot.

## Operational ownership and configuration

The municipality names a service owner, duty operator, database/hosting owner, identity administrator, dispatch supervisor and registered Information Officer, with deputies and an existing private escalation channel. Keep their contacts, infrastructure identifiers, maintenance calendar and recovery credentials in protected municipal records. Public repository data and all verification fixtures remain synthetic.

Start from the blank [environment inventory](../../.env.example), [identity/role matrix](../RBAC.md), [secret-source rules](../security/SECRETS.md) and [municipality onboarding](../ONBOARDING_MUNICIPALITY.md). Dotnet does not load `.env.example`. Export selected settings or mount private secrets; never create a filled env file in the repository, pass secrets in CLI arguments or print resolved Compose configuration.

| Configuration group | Production decision / safe unconfigured behavior |
|---|---|
| Runtime | `CIVICOPS_DEMO_MODE=false`, approved `PUBLIC_BASE_URL`, `AllowedHosts`, ingress TLS and explicit `CIVICOPS_TRUSTED_PROXY_NETWORKS`; demo is a separate synthetic installation |
| Storage | Prefer reviewed PostgreSQL 17 infrastructure with `CIVICOPS_STORAGE=postgres` and private `CIVICOPS_DATABASE_URL_FILE`; missing PostgreSQL credentials fail startup. File mode supports one process per private persistent data directory |
| Identity | Complete OIDC provider/client/role/tenant configuration or explicitly approved bootstrap access; partial OIDC configuration fails startup. Blank OIDC is disabled |
| Channels | Configure approved ElevenLabs/Zendesk credentials only after synthetic validation; absent credentials select labelled simulation/outboxes. Email remains outbox unless explicitly configured otherwise |
| Reasoning | Approved private vLLM URL/model/key; absent or failed inference uses bounded deterministic fallback. An AMD label does not verify hardware |
| Privacy | Information Officer contact, approved case/contact retention and notices; blank retention disables purge. Retention is not full cross-artifact erasure |
| Monitoring | `CIVICOPS_TELEMETRY=otlp` plus protected collector `OTEL_*` settings, independent probes and alert receiver; blank telemetry is off and explicit demo suppresses external OTLP |

OIDC, provider credentials, retention and Information Officer settings are process-wide. Tenant records do not provide separate provider-account or retention settings. Municipalities needing different policies/accounts must approve separate deployments or commission that configuration extension.

## Deploy and accept a release

The supported topology is **one active HTTP application instance**, including with PostgreSQL. The [deployment guide](DEPLOYMENT.md) contains the exact image, volumes, Secret references, TLS/proxy settings and network policy requirements. SSE, staff sessions/CSRF and quotas are process-local; restarting expires sessions. The Helm chart rejects replicas/HPA above one and uses `Recreate`; the owner must also prevent manual scaling or another active release against the same state.

Before an owner deploys, run the local build/contracts/static manifest gate:

```bash
dotnet build -c Release
dotnet test -c Release --no-build
./scripts/with_ephemeral_pg.sh dotnet test -c Release --no-build
./scripts/validate_deployment.sh
```

On the owner's Docker-capable host, with a private env file outside the checkout:

```bash
docker compose --env-file "$CIVICOPS_COMPOSE_ENV_FILE" -f deploy/compose/docker-compose.yml config --quiet
docker compose --env-file "$CIVICOPS_COMPOSE_ENV_FILE" -f deploy/compose/docker-compose.yml up --build -d
```

On an owner-managed cluster, ensure the referenced Secret, database, ingress/TLS and network policy dependencies exist before the migration hook runs:

```bash
helm lint deploy/helm/civicops --strict -f /run/civicops/values.yaml
helm upgrade --install civicops deploy/helm/civicops -n civicops --create-namespace \
  -f /run/civicops/values.yaml --wait --timeout 5m
```

These deployment commands are documented owner execution, not commands exercised on this host. Build/scan/sign an immutable image, approve its digest and resource limits, take a verified backup, record migration versions and rollback image, announce the maintenance window and stop/drain the previous instance. Helm rollback does not undo database migrations. Avoid overlapping blue/green releases; green is validated against isolated synthetic storage, then cut over only after blue stops.

After deployment, set `CIVICOPS_BASE_URL` to the approved origin without credentials and check:

```bash
curl --fail --silent --show-error "$CIVICOPS_BASE_URL/health/live"
curl --fail --silent --show-error "$CIVICOPS_BASE_URL/health/ready"
```

Verify storage readiness and intended provider modes, actual HTTPS cookie/HSTS behavior and issuer callback/logout. Through authorized staff sessions, check tenant isolation, a synthetic consented report, supervisor review/assignment, status/update approval, outbox reconciliation and `/api/audit/verify` for every municipality. Check a live collector sample and independent probe before accepting traffic. A degraded provider response with HTTP 200 is intentional; liveness alone proves neither durable intake nor provider delivery.

## Rotate credentials and service accounts

Maintain an inventory of credential owner, scope, expiry, consumers, mounted source and next rotation date in the municipal secret manager. Use the [tested `_FILE` sources](../security/SECRETS.md); Azure Key Vault/HashiCorp direct adapters are documented-only. Mount versioned secrets atomically and keep old audit-verification keys in independently recoverable restricted custody.

1. For a routine provider/OIDC/database rotation, the authorized owner obtains the replacement in the existing account, reviews its permissions and expiry, then updates the private secret source. Do not print or photograph either value. Apply a planned single-instance restart to refresh cached OIDC/data-source/provider configuration; expect staff reauthentication.
2. Verify local readiness, the intended mode and an owner-approved synthetic provider/SSO check. Revoke the old external credential only after the replacement works; for suspected compromise, containment/revocation takes priority and follows [incident response](INCIDENT_RESPONSE.md). Automatic provider OAuth refresh is not implemented.
3. For a CivicOps service account, sign into its municipality as admin. In Administration → Service accounts choose Rotate, or use `POST /api/admin/service-accounts/{id}/rotate` with `overlapMinutes` between 0 and 60 and an approved expiry. Transfer the once-shown replacement through the secret manager to its trusted client; verify only the permitted scope and tenant. The old token stops at the earlier of overlap end or original expiry. A second rotation replaces the previous overlap token. Revoke disables both immediately.
4. For local bootstrap users, Administration rotation/revocation invalidates their existing sessions; role edits take effect on the next request. Rotate the configured bootstrap-admin hash and restart when retiring the initial access path. OIDC role membership refreshes at re-sign-in/session expiry; changing directory membership does not instantly revoke an existing server ticket. During urgent staff compromise, stop/restart the app to clear all sessions after the issuer administrator contains the account.
5. Rotate audit HMAC signing key/id as a separately approved evidence operation. New exports use the current key; historical bundles require the corresponding retained key. Verify a new synthetic export and a historical bundle before retiring custody. Rotate backup age recipients while retaining identities needed to decrypt retained backups; drill recovery before deleting old identities.

Supported scope/expiry/overlap behavior is in [RBAC.md](../RBAC.md), [ServiceAccountStore.cs](../../products/civicops/Services/ServiceAccountStore.cs) and [service-account tests](../../tests/CivicOps.Tests/ServiceAccountTests.cs). External account rotation and client rollout remain owner execution.

## Scale and migrate

Scale vertically within the measured municipal capacity envelope first: CPU/memory, database connections, storage IOPS and bounded provider budgets. Use [short synthetic load tests](LOAD_TEST_RESULTS.md) on isolated storage. PostgreSQL serializes snapshot operations under a database-wide lock and each outbox leader needs an additional dedicated connection; CPU alone may not remove the bottleneck. Do not raise timeouts/quotas or readiness backlog limits to hide a downstream outage. Multi-node HTTP scaling requires distributed sessions/DataProtection, quotas and tenant SSE notification/reconnect work plus failover/isolation tests before chart guards change.

Embedded migrations are immutable/checksummed. Add a new numbered migration, test it against a disposable database and use backward-compatible expand/contract changes. With the private database source already configured:

```bash
CIVICOPS_STORAGE=postgres dotnet run --project products/civicops -- --migrate
```

Use a reviewed DDL role for migration and verify narrower runtime grants on the same schema/logical data root. Startup still verifies/applies the ledger. A checksum mismatch is an integrity incident; do not edit the ledger. For a stopped file installation moving to an empty PostgreSQL target:

```bash
CIVICOPS_STORAGE=postgres dotnet run --project products/civicops -- \
  --import-file-store /absolute/private/stopped-file-data
```

Take a verified source backup first. Import refuses a nonempty target, verifies snapshots inside one transaction and preserves historical hashes. It is not a CSV/import merge facility and makes no provider calls. Reconcile tenant/case/workforce/identity/training/outbox counts, permissions and audit heads before cutover. Full details: [STORAGE.md](STORAGE.md).

## Backup, restore and integrity evidence

Follow [BACKUP_RESTORE.md](BACKUP_RESTORE.md) for checksum, empty-target, permission and optional age-encryption requirements. File backups require the application stopped; PostgreSQL logical backups can run online. Stop every writer before a restore and retain damaged storage as restricted evidence. Database roles/grants/TLS/server configuration must be recovered separately.

```bash
./scripts/backup.sh /absolute/private/backups/new-backup
# Select a new empty data directory/database through private configuration first.
./scripts/restore.sh /absolute/private/backups/new-backup
# Local synthetic recovery contract, covering both stores:
./scripts/smoke_restore.sh
```

Restart, validate migrations/readiness, compare counts and each tenant's verified audit head, validate permissions and synthetic lifecycle, then the service owner authorizes traffic. Record backup age, restore duration and observed loss. Proposed RPO ≤15 minutes / RTO ≤60 minutes require approved backup schedules/PITR and real drills; they are not established by the local smoke. Production backup scheduling, off-site immutable retention and PITR remain owner-pending. [Signed audit exports](AUDIT_EXPORT.md) detect tampering with separately protected keys; local hash chains have no external anchor.

## Common alerts and actions

Adopt thresholds from [SLO.md](SLO.md) only after configuring actual collector names, probe denominators and alert routing. Never paste resident text or credentials into alert tickets; use opaque correlation/trace identifiers and sanitized counts.

| Alert / observation | First actions | Recovery evidence |
|---|---|---|
| Readiness 503; liveness 200 | Inspect safe storage/migration/backlog reasons; database reachability, disk/inodes/permissions and pool capacity; preserve data. Do not repeatedly restart a healthy process to clear a storage fault | Storage healthy, authoritative queue counts fresh, migration ledger and tenant audit verify |
| Both probes fail | Check owner process/container/node events and resources; restart only after preserving relevant evidence and confirming one active instance | Independent durable-intake probe and staff sign-in succeed |
| Intake budget burn / p95 triage >30 s | Trace accepted/failed intake; check database lock/IO and AI timeout/circuit/fallback. Pause discretionary releases and use the documented human intake alternative | Eligible-attempt availability and latency recover with adequate samples |
| Configured AI fallback >20% | Check bounded failure/schema-rejection labels, private vLLM health/GPU owner signals and catalogue compatibility. Deterministic intake continues; dispatch still requires humans | Owner synthetic decisions valid; fallback ratio recovers; AMD measurements separately recorded |
| Live outbox >5/15 min; permanent/exhausted dead letters | Inspect safe status/error labels, provider incident/429 and credential expiry; correct the cause, reconcile existing tickets, then supervisor/admin retries the affected dead letters | Pending ages decrease, case order preserved and actual delivery markers reconciled |
| Backlog above readiness limit | Assess durable storage capacity and downstream recovery; escalate before residents lose intake. Raising the limit requires a capacity decision | Backlog below threshold and intake probe healthy |
| Audit verification failure / migration checksum mismatch | S1: isolate affected writes, preserve storage/export/log evidence and page the security/Information Officer roles; never recompute hashes to conceal a gap | Investigated cause, verified recovery and owner-approved reopening |
| Missing telemetry / `MetricsUnavailable` | Check collector and storage independently; gauges can retain old values during failures. Restore fresh sampling before asserting SLO success | New successful sample plus independent probe |
| Municipal review breaches | Dispatch supervisor reviews staffing, urgent hazards and crew eligibility; do not auto-approve or bypass certificates | Explicit human decisions and municipal escalation recorded |
| Retention purge failure / contested deletion | Preserve evidence; review queue protection, legal holds and approved retention. Escalate to Information Officer | Correct local purge evidence plus separate audit/provider/backup handling |

## Provider outage playbooks

| Dependency | Available behavior / operator action |
|---|---|
| AMD vLLM / LLM | Shared decision deadline, at most two attempts and deterministic fallback preserve intake. Three transient failures open a 30-second circuit with one recovery probe. Review hazard/routing output with humans; restore owner-managed inference and validate synthetic schema/latency. Do not claim fallback is a measured AMD result |
| ElevenLabs | Form/Open311 remain available; the resident banner reports degraded/unverified mode. Check provider status and owner channel/network configuration. A callback with an uncertain accepted response must be reconciled before another attempt; automatic calling is not a fallback. Keep residents' alternative municipal channels visible |
| Zendesk | Preserve live configuration and durable intents. Bounded retries/backoff lead to visible dead letters; local human workflow remains authoritative. After recovery reconcile ticket/event markers and retry corrected failures; at-least-once delivery still carries downstream duplicate risk |
| Email/SMTP | Outbox mode is labelled; an SMTP failure does not establish delivered resident contact. Review the safe failure and use the municipality's approved alternative communications procedure. SMTP is optional and unverified here |
| Identity provider | Existing sessions work until expiry/restart; new SSO may fail. Use only a previously authorized, protected bootstrap administrator/user path. Never enable the demo role picker for production access; issuer restore and actual sign-in/logout need owner checks |
| Database/storage | Readiness fails closed; PostgreSQL never switches to files. Stop affected writes and use municipal manual continuity records under the same privacy controls, then recover verified storage. There is no automatic import of manual records |
| Collector | Intake remains available while exports fail; logs/probes are fallback evidence. Restore collector/network access and record the measurement gap |

**Do not enable demo or remove Zendesk credentials on a production queue to handle an outage.** The simulated client can complete pending intents with simulated ticket IDs; that would require explicit reconciliation before any live resend. No separate outbox-pause switch is implemented. For urgent compromise, stop the application/preserve state before owner-managed external revocation and restart with reviewed replacement configuration. Changes to provider account assets or live writes require the owner's operational authorization and are not performed by this repository's verification suite.

Implementation references: [IntegrationOutbox.cs](../../products/civicops/Services/IntegrationOutbox.cs), [ProviderCircuit.cs](../../products/civicops/Services/ProviderCircuit.cs), [AiReasoningClient.cs](../../products/civicops/Services/AiReasoningClient.cs), [SecretProvider.cs](../../products/civicops/Services/SecretProvider.cs), [CivicOpsTelemetry.cs](../../products/civicops/Services/CivicOpsTelemetry.cs). For security/privacy incidents, use [INCIDENT_RESPONSE.md](INCIDENT_RESPONSE.md).
