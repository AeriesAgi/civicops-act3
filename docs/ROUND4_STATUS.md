# CivicOps round 4 — 8 October 2026

Branch `grok/civicops-finish`, baseline `798d98b`. All work is local in this repository with prescribed workspace-temporary outputs. No push, remote/PR/merge/rebase/force/amend, Docker execution, real-provider write, SMTP, LLM write request, signup, real PII or filled environment file was used.

## Delivered against the six requested items

1. **OIDC/SSO:** ASP.NET Core OIDC 10.0.12, generic/Entra/Google presets, authorization code + S256 PKCE, explicit claim/group role maps, municipality/crew validation, opaque server-side staff sessions, Secure/HttpOnly/Lax cookies, expiry and sign-out. Demo role selection exists only in explicit demo and is visibly synthetic/unverified. Local bootstrap users support non-SSO installations; old service credentials remain compatible. In-process discovery/JWKS/token tests exercise the actual handler, including PKCE, role-based access, invalid roles/municipalities/nonce/signature and sign-out. No live issuer was called.
2. **RBAC:** one code matrix plus a tested [RBAC table](RBAC.md), 20 policies, 76 explicit method/route pairs and 608 role/route authorization comparisons. Startup rejects unmapped routes and fallback denies access. All eight roles are covered. High/critical hazards or Urgent cases require supervisor/admin approval and assignment; overrides require supervisor/admin. Crew writes remain assignment-bound even when combined with auditor read permissions. Auditors alone have read/export without mutations. Cookie writes and bootstrap/demo login require CSRF. Quotas retain previous invalid-credential behavior, include municipality, and retain bounded per-conversation partitions for service-account tools. Signed-in residents use a report-only simulated-chat facade; provider tools remain agent-scoped and the facade is disabled before any provider call in live mode.
3. **Municipalities:** directory stores id/name/slug/departments/language/time zone/service-area/optional resident host. Two synthetic demo municipalities have independent workforce. Incidents, training, workforce, audit, deliveries, voice receipts, SSE and quotas are partitioned. Principal binding or resident `/m/{slug}`/host routing precedes store access; body tenant fields never select a partition. Default-only legacy migration preserves old hashes; copying old snapshots elsewhere fails closed. Background retention/delivery/recovery iterate tenant scopes. Locale defaults apply to new intake while retaining old submission fingerprint/retry semantics.
4. **Service accounts:** admin-created, municipality-bound, explicit scopes, random tokens shown once, hashes only, reviewed expiry, 0–60 minute capped overlap rotation and immediate revocation. Per-tenant identity audit chains mirror recoverably into incident audit. Local staff role edits apply on the next request; token rotation/revocation invalidates their sessions. Existing environment dispatcher/training/tool credentials remain default-tenant bootstrap paths.
5. **Administration:** role-gated view for municipality metadata, local users/roles, service accounts, catalogue revisions, crews, workers/certifications. Versioned catalogue edits enforce schema bounds/references, retain classifier codes, become live in that tenant's discovery/agent/routing/training contracts, survive restart and reject tampering. Accessible controls, permission-driven staff actions, tenant-switch clearing, mobile layout and lifecycle flows pass real Chrome checks. [admin-console.png](media/admin-console.png) was regenerated with `CIVICOPS_UPDATE_MEDIA=1` and inspected; 1440×1850, below 1 MB. Seven round-3 images are unchanged.
6. **Contracts/docs:** generated OpenAPI includes policies/roles, cookie and scoped bearer alternatives, CSRF and tenant metadata. Both `.env.example` inventories have 73 unique blank entries. New ElevenLabs tool plans use an Authorization workspace-secret locator containing a service bearer; old headers remain runtime bootstrap-compatible. README, setup, API, security and architecture docs describe current features and limits.

## Local commits

| Commit | Subject |
|---|---|
| 807d2c3 | Add a repository-owned fail-fast verification entry point |
| 4375cc4 | Add tenant-aware staff sign-in and accessible administration console |
| 410f6b9 | Isolate municipality stores, audit chains and workforce administration |
| c53cf1b | Verify tenant isolation across intake, tools, lifecycle and streams |
| 8d50095 | Add tenant-bound OIDC sessions and scoped service account credentials |
| ce8b1df | Reject legacy tenant rebinding and verify administrator isolation |
| 8115c54 | Use server permissions for crew views and supervisor decisions |
| 9bec5d9 | Verify enterprise administration flows and publish inspected console capture |
| eab61fb | Describe bootstrap access token lifetime accurately |
| e9a508f | Validate staff session lifetime and municipality crew claims |
| 03a2de8 | Verify OIDC PKCE sessions and scoped credential isolation with a local issuer |
| 61d660d | Define explicit role policies and guard tenant-bound staff resources |
| 80f4886 | Activate audited administration and live municipality catalogue revisions |
| 3530074 | Publish enterprise authentication contracts and scoped provider tool plans |
| 396ee97 | Record round-four security boundaries and current browser evidence |
| cbbc839 | Document round-four verification, identity setup and owner validation limits |
| 6f9b3c6 | Make isolation and qualification tests own background delivery scheduling |
| 071b50c | Keep combined crew and auditor write privileges assignment-bound |
| e81c09f | Keep simulated chat available to authenticated municipal residents |
| 699bc3a | Preserve resident simulation and conversation quotas with enterprise roles |

The final handoff commit follows this table; `git log 798d98b..HEAD --oneline --reverse` includes it.

## Implemented-and-tested evidence

| Check | Local result |
|---|---|
| Release build | 0 warnings, 0 errors |
| Full Release tests | **755 passed, 0 failed, 0 skipped**; baseline 625 retained, 130 added |
| New test distribution | Identity/OIDC/service credentials 57; tenancy 39; RBAC 20; admin/catalogue 11; resident chat compatibility 3 |
| Route permission enumeration | 76 registered method/route pairs × 8 roles = **608 comparisons**, no AllowAnonymous metadata, one explicit policy each |
| Lifecycle HTTP smoke | Passed non-demo bootstrap auth, idempotency, versioning, workflow, Open311 and audit |
| End-to-end HTTP smoke | Passed simulated channel → agents → outbox → human assignment → reviewed comms → signed webhook/replay/privacy; **10 audit events verified** |
| Browser smoke | **82 passed**, including all previous 51 plus 31 enterprise/admin/permission/resident-chat checks |
| Opt-in media browser smoke | **80 passed** when capturing the inspected admin view; subsequent resident-chat checks pass in the final 82-check run; old seven PNGs untouched |
| NuGet vulnerability scan | Both projects clean, including transitive packages |
| Gitleaks history/directory | Clean at implementation verification; repeated by the final harness after this commit |
| Source/assets | JavaScript/shell/Python checks passed; 22 JSON assets and both YAML files parsed; 73 unique blank placeholders in each env inventory |
| Containers | Existing Dockerfile/Compose retained; YAML parsed statically, daemon never used; runtime unverified |

Exact commands with the required environment:

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TMPDIR=/workspace/civicops/tmp
export CIVICOPS_PLAYWRIGHT_MODULE=/workspace/civicops/tmp/browser-tools/node_modules/playwright-core/index.mjs
dotnet build -c Release
dotnet test -c Release --no-build --logger 'trx;LogFileName=round4.trx' --results-directory "$TMPDIR/test-results"
./scripts/smoke_lifecycle.sh
./scripts/smoke_e2e.sh
./scripts/smoke_browser.sh
CIVICOPS_UPDATE_MEDIA=1 ./scripts/smoke_browser.sh
dotnet list package --vulnerable --include-transitive
gitleaks detect --source . --log-opts="--all" --redact
gitleaks dir . --redact
git diff --check
git status --short
# Final mandatory gate runs on the committed clean tree:
/workspace/civicops/verify.sh
```

The mandatory baseline verifier was invoked first. Its initial external shell quoting error prevented the browser/scan portion; those checks were run directly and passed before implementation. A repository-owned fail-fast `scripts/verify.sh` was committed. The external verifier subsequently passed its syntax check without any out-of-repository edit by this round. The final mandatory command is repeated on the clean handoff commit; its output is reported in the final response.

## New environment settings

All placeholders stay blank in both inventories; dotnet still reads exported configuration only.

- `CIVICOPS_OIDC_PROVIDER`, `CIVICOPS_OIDC_AUTHORITY`, `CIVICOPS_OIDC_CLIENT_ID`, `CIVICOPS_OIDC_CLIENT_SECRET`, `CIVICOPS_OIDC_DIRECTORY_ID`.
- `CIVICOPS_OIDC_ROLE_CLAIM`, `CIVICOPS_OIDC_GROUP_CLAIM`, `CIVICOPS_OIDC_TENANT_CLAIM`, `CIVICOPS_OIDC_ROLE_MAP`, `CIVICOPS_OIDC_GROUP_MAP`.
- `CIVICOPS_OIDC_TENANT_ID`, `CIVICOPS_OIDC_HOSTED_DOMAIN`, `CIVICOPS_OIDC_SESSION_MINUTES`.
- `CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH`, `CIVICOPS_BOOTSTRAP_ADMIN_TENANT`.

Exact formats/defaults and setup are in [RBAC.md](RBAC.md). No new municipality/provider token variable is needed: an administrator creates tenant-bound service accounts and stores their issued token in a trusted provider/backend secret manager. The synthetic one-command run remains zero-configuration.

## Documented-only and owner-pending

- **Live deployment:** actual Entra/Google/generic registration, directory claims and MFA/access policies, issuer logout, TLS/ingress/forwarding and institutional security/privacy/accessibility review. The local issuer proves the handler flow, not the owner's infrastructure. Google supports local sign-out; no provider end-session endpoint is advertised.
- **Session scale and directory change:** tickets are process memory and expire on restart; no distributed session store. SSO roles are the signed snapshot at login until expiry/re-sign-in; no Graph group-overage resolution, directory polling, continuous access evaluation or refresh-token flow. Bootstrap local user role/revocation changes do apply per request.
- **Provider/hardware:** live ElevenLabs/Zendesk auth, token rotation, SDK/microphone/language/telephone/delivery, separate per-tenant provider workspaces and actual AMD hardware/performance remain unverified. Outbound provider credentials remain instance configuration. Everything here used deterministic reasoning, simulated transports/outboxes or local fake handlers.
- **Persistence/privacy:** one process per durable JSON data directory; no database/distributed transaction, directory fsync, external audit anchoring, complete resident identity, complete erasure across artifacts/providers or verified backup/restore policy. Retention preserves audit/integration/training evidence.
- **Publication/event:** remote CI and container runtime unverified. Owner handles publication-history clearance, token scopes/repository visibility, event eligibility, deployed URL, video, slides, cover and live AMD evidence. No remote operation was performed.
