# Round 5 — security hardening and POPIA privacy

Implemented locally on `grok/civicops-finish`, 8 October 2026. Synthetic data only; no push, remote/PR/merge/rebase, Docker execution, real identity/provider/LLM/SMTP writes or sign-ups. This is engineering evidence, not certification or municipal approval to process real resident data.

## Mandatory baseline

Read run01–04 handovers, ROUND2_STATUS and README first. `/workspace/civicops/verify.sh` at `c0db3bb` was all green: 0 warnings/errors, 755 tests, 82 browser checks, lifecycle and E2E (10 audit events), both secret scans, pre/post Git status 0 dirty. No repair commit was needed.

## Delivered per requested item

| Item | Implemented and tested | Limits / owner work |
|---|---|---|
| 1. Headers/CSP | Fresh 192-bit script nonces, no unsafe-inline/eval, conditional pinned widget/SDK origins and SRI, scoped widget style nonces, production HTTPS HSTS, nosniff, referrer/permissions/frame controls, COOP/CORP. Browser and actual widget smoke report zero CSP violations. | Trusted HTTPS ingress, live CDN/microphone/provider sessions remain unverified. |
| 2. CSRF | Framework antiforgery validates cookie writes and login, identity-bound tokens, missing/cross-browser tokens rejected. Bearer/tool/HMAC authentication is deliberately exempt and documented. | Issuer/host policies and deployed SSO require owner validation. |
| 3. Validation/quotas | All 81 method/route pairs inventoried; bounded/chunked JSON, header/path/query limits, duplicate fields, malformed JSON, Unicode controls, canonicalisation, field allowlists, safe ProblemDetails. Tenant/principal/IP budgets for intake, auth, tools and training; shared tool cap prevents conversation rotation bypass. | In-process quotas reset on restart; ingress/distributed protection remains owner work. |
| 4. Secrets | `ISecretProvider`, bounded env and mounted-file sources, explicit file precedence/fail-closed errors, options wiring, credential/PII log redaction and sentinel readiness/log/exception tests. | Azure/HashiCorp adapters are documented-only; configured unsupported adapters fail closed. CSI-mounted files work today. |
| 5. ASVS | All 259 ASVS 4.0.3 L2 requirements, including inherited L1, individually mapped across V1–V14; 92 met, 67 partial, 68 owner, 32 N/A. Cheap gaps fixed. | Local gap review only; no independent L2 certification. |
| 6. CI security | Latest-recommended analyzers/Release warnings as errors; CodeQL C#/JavaScript, dependency review, explicit advisory rejection, npm audit, pinned CycloneDX 6.2.0 generator, 56-component specification-1.6 SBOM and CI artifact declaration. | Workflows statically validated; remote execution/features/branch protection pending. Host/runtime/container/model inventories excluded from this package SBOM. |
| 7. Disclosure/threats | SECURITY.md defines private reporting/response targets. STRIDE has 48 rows across resident intake, tools, webhooks, LLM, dispatcher, training, admin and storage. | Maintained reporting contact, institutional response staffing and notification decisions remain owner-pending. |
| 8. POPIA | Exact versioned purpose consent; optional contacts separately stored with audit-bound digest/migration; closed-case contact retention/tombstones; admin request→verification→access JSON/correction/anonymisation UI/API; redaction of backend model/log/audit/voice context; Information Officer configuration; eight-condition map and DPIA template. | Identity notes are admin assertions; direct provider speech precedes backend redaction. Legacy immutable audit, provider/backups/export copies and statutory obligations need Officer-led handling. Local anonymisation is not complete erasure. |

The existing 755 tests are retained; 132 cases were added. Existing browser checks and tracked round-3/4 media are retained. The default synthetic one-command demo still requires no provider configuration.

## Exact local verification

Environment:

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TMPDIR=/workspace/civicops/tmp
export CIVICOPS_PLAYWRIGHT_MODULE=/workspace/civicops/tmp/browser-tools/node_modules/playwright-core/index.mjs
export DOTNET_PROCESSOR_COUNT=2
```

The processor-count cap keeps the shared build box modest; it is verification tooling, not a new application setting.

| Command | Result |
|---|---|
| `dotnet build -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test -c Release --no-build --logger 'trx;LogFileName=round5.trx' --results-directory /workspace/civicops/tmp/round5-test-results` | **887 passed**, 0 failed/skipped |
| `./scripts/smoke_lifecycle.sh` | Passed; non-demo consented intake, lifecycle, authenticated identity and Open311 |
| `./scripts/smoke_e2e.sh` | Passed; **10 audit entries verified** |
| `./scripts/smoke_browser.sh` | **89 checks**, zero CSP/runtime violations; temporary screenshots inspected |
| `./scripts/smoke_security_privacy.sh` | **25 checks** passed |
| `./scripts/smoke_widget_csp.sh` | **8 checks** passed; actual pinned widget, 3 controls, 40,864 theme characters, zero CSP/runtime errors or provider requests sent |
| `./scripts/security_dependencies.sh` | **2 projects, 0 vulnerable direct/transitive packages; 0 repository npm manifests** |
| `dotnet list CivicOps.sln package --vulnerable --include-transitive --format json --output-version 1` | Invoked by gate; report validated and advisory failures rejected |
| `./scripts/sbom.sh` | **56 components, 81,392 bytes**; CycloneDX 1.6; graph validated |
| Isolated npm audit of pinned SDK/widget/worklet/Playwright packages | **0 vulnerabilities**; no install scripts executed |
| `bash -n scripts/*.sh`; `node --check` for app/admin/browser/widget scripts | Passed |
| `$TMPDIR/bin/actionlint -color=false .github/workflows/*.yml` | All **3 workflows** passed |
| PyYAML 6.0.3 parse of Compose and workflows | **4 YAML files** passed; Docker statically validated only |
| Markdown link checks; ASVS/STRIDE inventory; blank examples | Passed; **259 / 48** rows; both examples **91 blank placeholders** |
| `gitleaks detect --source . --log-opts="--all" --redact` | Clean; final harness scans all local commits |
| `gitleaks dir . --redact` | Clean |
| `git diff --check`; `git status --short` | Clean after local commits |
| `/workspace/civicops/verify.sh` | Final committed-head gate is required; see completion report for exact head and results |

The first full integration run found expected consent-fixture updates, error-envelope casing and empty-logout regressions; these were corrected while retaining assertions. One background delivery race in an identity attribution test was fixed by making that test own its delivery schedule. No security assertion was relaxed.

## New configuration

Both `.env.example` files contain blank placeholders only:

- `CIVICOPS_AUTH_LIMIT`, `CIVICOPS_TOOL_LIMIT`, `CIVICOPS_SECRET_PROVIDER`
- `CIVICOPS_INFORMATION_OFFICER_CONTACT`, `CIVICOPS_PII_RETENTION_DAYS`
- Thirteen mounted credential variants: `CIVICOPS_OIDC_CLIENT_SECRET_FILE`, `CIVICOPS_LLM_API_KEY_FILE`, `ELEVENLABS_API_KEY_FILE`, `CIVICOPS_ELEVENLABS_TOOL_SECRET_FILE`, `ELEVENLABS_WEBHOOK_SECRET_FILE`, `ZENDESK_API_TOKEN_FILE`, `ZENDESK_OAUTH_TOKEN_FILE`, `ZENDESK_WEBHOOK_SECRET_FILE`, `CIVICOPS_EMAIL_SMTP_PASSWORD_FILE`, `CIVICOPS_DISPATCH_TOKEN_FILE`, `CIVICOPS_DISPATCHERS_FILE`, `CIVICOPS_TRAINING_CLIENTS_FILE`, `CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH_FILE`.

No configuration is necessary for the explicit synthetic demo. Retention is disabled unless an approved period is configured. Missing Information Officer contact is labelled owner-pending.

## Before an enterprise resident pilot

Owner approval remains required for lawful basis, notices/approved translations, Information Officer registration/contact, operator agreements/cross-border assessment, identity-verification procedures, legal holds and provider/backup/audit deletion. HTTPS/trusted ingress, issuer MFA/live SSO, encryption/backups, incident response and independent security testing are not locally attested. Durable storage remains single-process JSON; contact/case and privacy/audit files are individually atomic, not one transaction; mismatch recovery fails closed. Distributed storage/sessions/quotas, external audit anchoring and provider OAuth refresh remain open. Actual AMD hardware evidence, deployment, live synthetic provider validation, remote CI and hackathon publication/eligibility/video/pitch assets remain owner-pending.

Evidence: [HTTP security](security/HTTP_SECURITY.md), [input inventory](security/INPUT_LIMITS.md), [secret sources](security/SECRETS.md), [ASVS](security/ASVS_L2.md), [STRIDE](security/THREAT_MODEL.md), [CI/SBOM](security/CI_SECURITY.md), [POPIA](privacy/POPIA.md), [DPIA](privacy/DPIA_TEMPLATE.md), [API](../products/civicops/docs/api.md), [SECURITY](../SECURITY.md).
