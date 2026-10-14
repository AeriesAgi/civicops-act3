# Round 6 — PostgreSQL, recovery, signed audit export and observability

Implemented locally on `grok/civicops-finish`, 8 October 2026. Synthetic data only. No push, remote/PR/merge/rebase, Docker execution, signup, or live provider/identity/LLM/SMTP writes were performed. The enterprise storage and operational capabilities below are implemented; municipal deployment approval, real workload capacity and production disaster recovery remain separate owner responsibilities.

## Mandatory baseline

Read run01–05 handovers, `ROUND2_STATUS.md` and README before work. The initial `/workspace/civicops/verify.sh` at `f676093` was all green: Release build 0 warnings/errors, **887 tests**, **89 browser checks**, lifecycle and E2E (10 verified audit entries), both gitleaks scans and clean Git status. No baseline repair commit was required. Existing tests and the round-3/4 visual assets are retained.

## Delivered per requested item

| Item | Implemented and tested | Deployment limits / owner work |
|---|---|---|
| 1. PostgreSQL | `CIVICOPS_STORAGE=file|postgres`, bounded secret-provider URL/connection-string support, pinned Npgsql 10.0.3. Embedded SQL migrations run at startup under an advisory lock with version/checksum ledger; explicit `--migrate` also works. Every durable store uses the selected backend: tenants, incidents/contacts/consent, audit chain, outbox/receipts/artifacts, catalogue, crews/workers, training, accounts/bootstrap users and DSAR. Case/contact/audit/delivery intents commit in one database transaction. Fresh reads and optimistic versions preserve cross-instance concurrency. Shared file/PG contracts and transactional file import are tested; CI has PostgreSQL 17.11 service job. | Database-wide write serialization and full snapshots favour correctness over throughput. Sessions, quotas and SSE remain instance-local. Delivery is at least once with reconciliation. Remote CI has not run. File mode remains one process with individually atomic files. |
| 2. Recovery | Checksummed manifests, stopped/locked file tarballs, PostgreSQL custom-format dumps and single-transaction restore into empty targets. Safe archive extraction, explicit database targeting, mounted-secret precedence and optional age encryption. Both recovery drills restore exact HTTP records/counts and verified audit heads; file artifacts match byte-for-byte. Corrupt payload and wrong encryption key fail closed. | Production scheduling, off-site retention, encryption-key custody, continuous WAL/PITR and measured RPO/RTO are owner-pending. GPG is documented-only. |
| 3. Audit export | Auditor-authorised ZIP/local endpoints and CLI produce JSONL plus HMAC-SHA256 signed manifest with tenant, counts, chain head, timestamp, key ID, length and SHA-256. Key comes from the existing secret provider; absence disables export honestly. Offline verifier checks signature/full chain; tamper, reordering, truncation, wrong key, tenant/role/CSRF, links and output races are tested. Exclusive local files are application-level append-only. | HMAC verifiers hold signing power. Separate trust domains, external checkpoints, historical-key custody and S3 Object Lock/Azure immutable blob are documented deployment work. A host administrator can replace local artifacts. |
| 4. Observability | OpenTelemetry traces, metrics and logs; ASP.NET Core, HttpClient and native Npgsql signals; agent/provider/outbox spans; intake, triage, fallback, delivery/backlog/age, SLA and audit gauges. Standard OTLP environment configuration, off/console demo modes. JSON logs preserve structured fields/scopes and redact known secrets/detectable PII. W3C trace/correlation propagation and ProblemDetails echo are tested. A real loopback OTLP collector receives all three signals with no synthetic PII/secret sentinels. | Collectors, dashboards, alerts and access/retention/egress controls are documented-only/owner-pending. Redaction is heuristic; unlabeled names/arbitrary prose need operational controls. Retained gauge values during failed sampling require failure-log/readiness alerts. |
| 5. Health | `/health/live`, `/health/ready`, compatible `/healthz`, and static Kubernetes startup/readiness/liveness example. Readiness checks storage, migration checksums and fresh global outbox backlog within bounded checks; provider failures/simulation/unverified status are degraded without failing otherwise available intake. Global probes bypass tenant/catalogue/credential database reads for anonymous, bearer and cookie requests. Real PostgreSQL stop/start proves live=200, ready=503, host survival and recovery. Compose forwards optional storage/audit/telemetry settings; Docker publish now includes the portable embedded schemas. | Docker/Compose/Kubernetes assets were validated statically; no daemon or deployed cluster was used. The Kubernetes example uses ephemeral demo data and an owner-supplied image; Compose requires owner database/collector hostnames and secret/export mounts. |
| 6. Degradation | Explicit unavailable/slow fake AI, ElevenLabs and Zendesk tests on both backends preserve intake/audit and degraded readiness. Deterministic AI fallback, bounded provider calls, transient-failure circuit breakers, single recovery probe and exponential retry with bounded jitter. PostgreSQL polling workers survive disconnects; transaction cleanup preserves the original storage failure. Accessible degraded UI banner is covered by browser smoke. | No live provider calls were made; municipal/provider accounts, network egress and live synthetic validation remain owner work. |
| 7. Operations docs | [Storage](operations/STORAGE.md), [recovery/RPO/RTO](operations/BACKUP_RESTORE.md), [audit format/verification/immutability](operations/AUDIT_EXPORT.md), [signals/PromQL/KQL](operations/OBSERVABILITY.md), [SLIs/SLOs/error budgets/alerts](operations/SLO.md), plus current README/API/RBAC/architecture/secret/privacy/security guides. The 259-row ASVS review now treats two previously inapplicable SQL requirements as partial: **92 met / 69 partial / 68 owner / 30 N/A**; all 48 STRIDE rows retained. | Proposed intake 99.9%, triage p95 ≤30s, delivery 95% ≤5m/99% ≤15m and audit 100% objectives are not measured production achievements. RPO ≤15m/RTO ≤60m are proposed pilot targets requiring owner approval and infrastructure drills. The ASVS inventory is not certification; historical round-5 results remain unchanged. |

## Exact local verification

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TMPDIR=/workspace/civicops/tmp
export CIVICOPS_PLAYWRIGHT_MODULE=/workspace/civicops/tmp/browser-tools/node_modules/playwright-core/index.mjs
export DOTNET_PROCESSOR_COUNT=2
```

The processor cap keeps builds modest on the shared box. Disposable PostgreSQL clusters use the current user, loopback/random high port, private socket, 32 MB shared buffers and 30 connections; the wrapper tears them down. No system database was used. No load test was added.

| Command | Result |
|---|---|
| `dotnet build -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test -c Release --no-build --logger 'trx;LogFileName=round6-file.trx' --results-directory "$TMPDIR/test-results/round6-file-verified"` | **960 passed**, 0 failed/skipped, **4m57s**; all prior 887 cases retained |
| `./scripts/with_ephemeral_pg.sh dotnet test -c Release --no-build --logger 'trx;LogFileName=round6-postgres.trx' --results-directory "$TMPDIR/test-results/round6-postgres-verified"` | **1,019 passed**, 0 failed/skipped, **5m58s**: 59 PostgreSQL + 59 corresponding shared file + 901 remaining cases; **all 887 baseline names retained**, zero missing |
| `./scripts/smoke_lifecycle.sh` | Passed; consented non-demo intake, authenticated dispatch/lifecycle and Open311 |
| `./scripts/smoke_e2e.sh` | Passed; **10 audit entries verified** |
| `./scripts/smoke_browser.sh` | **91 checks**, zero CSP/runtime violations; temporary screenshots inspected, checked-in visual assets retained |
| `./scripts/smoke_security_privacy.sh` | **25 checks** passed |
| `./scripts/smoke_widget_csp.sh` | **8 checks**, zero CSP/runtime violations/provider requests |
| `./scripts/smoke_restore.sh` | **File 18 checks, PostgreSQL 21 checks**; each restores **2 cases and 12 verified audit entries**, PG includes actual runtime outage/recovery |
| `PATH="$TMPDIR/bin/age:$PATH" CIVICOPS_SMOKE_ENCRYPTION=age ./scripts/smoke_restore.sh` | **File 19 checks, PostgreSQL 22 checks**; wrong recovery key rejected |
| `python3 -m unittest discover -s scripts -p test_backup_restore.py` | **15 passed** |
| `./scripts/smoke_audit_export.sh` | **3 signed exports**, **4 events each**, truncation rejected; HTTP download/local output and offline CLI verified |
| `./scripts/smoke_telemetry.sh` | Actual loopback OTLP **traces/metrics/logs** received; agent/fallback/intake signals, resource/correlation/traceparent/ProblemDetails and sentinel redaction passed |
| `./scripts/with_ephemeral_pg.sh ./scripts/smoke_telemetry.sh postgres` | Native Npgsql **database.command traces**, **operation-duration/connection-count metrics** and safe pool label received; raw SQL/URL attributes and synthetic password/PII/provider-secret sentinels absent |
| `./scripts/security_dependencies.sh`; `dotnet list CivicOps.sln package --vulnerable --include-transitive --format json --output-version 1` | **2 projects, 0 vulnerable direct/transitive packages, 0 repository npm manifests** |
| `./scripts/sbom.sh` | **65 components, 92,959 bytes**, CycloneDX 1.6 inventory refreshed |
| `bash -n scripts/*.sh`; JavaScript syntax; Python AST | Passed |
| `$TMPDIR/bin/actionlint -color=false .github/workflows/*.yml`; YAML parsing | **3 workflows** and **5 YAML files** (workflows, Compose, Kubernetes) validated; Docker static only |
| Blank environment inventory | Both examples contain **115 blank placeholders**, including **24 new** variables; no values/tokens committed |
| `gitleaks detect --source . --log-opts="--all" --redact`; `gitleaks dir . --redact` | Final clean-head harness checks both; see completion report |
| `git diff --check`; `git status --short`; `/workspace/civicops/verify.sh` | Final committed-head gate follows this handover; see completion report for exact head and counts |

The first full runs exposed background delivery races in tests asserting exact case versions. Those fixtures now own delivery scheduling; the assertions and test cases remain intact. A real database outage exposed host shutdown from transaction-cleanup exceptions and database reads ahead of health handlers; both were corrected and regression-tested. Synthetic test diagnostics capture only exception type and code stack when a 500 occurs, excluding exception messages/payloads.

## New configuration

Both `.env.example` files keep all values blank:

- Storage/test/client tooling: `CIVICOPS_STORAGE`, `CIVICOPS_DATABASE_URL`, `CIVICOPS_DATABASE_URL_FILE`, `CIVICOPS_TEST_DATABASE_URL`, `CIVICOPS_PG_BIN`.
- Audit signing/output: `CIVICOPS_AUDIT_SIGNING_KEY`, `CIVICOPS_AUDIT_SIGNING_KEY_FILE`, `CIVICOPS_AUDIT_SIGNING_KEY_ID`, `CIVICOPS_AUDIT_EXPORT_DIR`.
- Operational controls: `CIVICOPS_TELEMETRY`, `CIVICOPS_PROVIDER_TIMEOUT_MS`, `CIVICOPS_OUTBOX_READY_MAX`.
- Telemetry: `OTEL_SERVICE_NAME`, `OTEL_SERVICE_VERSION`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_SDK_DISABLED`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`, `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`. The exporter also accepts the standard per-signal protocol/header/timeout and batching/interval variables documented in the observability guide.
- Backup encryption: `CIVICOPS_BACKUP_AGE_RECIPIENT`, `CIVICOPS_BACKUP_AGE_IDENTITY_FILE`.

`CIVICOPS_SMOKE_ENCRYPTION=age` selects the test-only encrypted drill. `CIVICOPS_EPHEMERAL_PG`/`CIVICOPS_EPHEMERAL_PG_DIR` are wrapper-owned test markers, not production configuration. The zero-configuration demo keeps file storage, deterministic/simulated dependencies and telemetry off; signing is disabled without a key.

## Before a resident pilot / hackathon publication

Production managed database/TLS/backups, restore roles/key custody, external audit trust/immutability, collectors/dashboards/alert routing, capacity evidence and distributed identity/session/quota/refresh infrastructure remain owner-pending. Existing POPIA lawful-basis/Officer/operator agreements/identity checks/retention and complete erasure limits remain. Live municipal SSO/provider validation and provider OAuth refresh, trusted HTTPS ingress, independent security review, actual AMD hardware evidence, deployment, remote CI and hackathon publication/eligibility/video/pitch assets have not been attested locally. No data or synthetic test result establishes municipal certification or achieved production SLOs.
