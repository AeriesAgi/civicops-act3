# CivicOps identity, permissions and municipality isolation

Identity and permissions are implemented and locally tested. Live municipal Entra/Google/generic accounts, provider authentication and deployed HTTPS remain **owner-pending/unverified**. No production readiness attestation or verified resident identity is claimed. Persistence now supports the default single-process file store and opt-in PostgreSQL with tested cross-instance transaction/version contracts; staff sessions, quotas and SSE notifications remain process-local. [Storage and scaling boundaries](operations/STORAGE.md).

## Single permission matrix

`Services/PermissionMatrix.cs` defines roles, policies and every HTTP method/route explicitly. Startup fails for an unmapped endpoint; the fallback policy denies access. Tests enumerate all mapped routes, reject `AllowAnonymous`, check exactly one named policy, and authorize every role/route pair against this matrix. Roles are additive when an administrator deliberately assigns more than one. Public resident intake has a named policy and receives municipality routing without requiring a resident account.

| Policy | resident | agent | dispatcher | supervisor | crew | auditor | admin | training-client |
|---|---|---|---|---|---|---|---|---|
| public | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| identity-public | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| authenticated | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| resident-read | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| resident-write | ✓ | ✓ | ✓ | ✓ | — | — | ✓ | — |
| operations-read | — | — | ✓ | ✓ | — | ✓ | ✓ | — |
| incident-read | — | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| workforce-read | — | — | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| staff-identity | — | — | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| dispatch | — | — | ✓ | ✓ | — | — | ✓ | — |
| work-status | — | — | ✓ | ✓ | ✓ | — | ✓ | — |
| supervise | — | — | — | ✓ | — | — | ✓ | — |
| audit | — | — | — | ✓ | — | ✓ | ✓ | — |
| admin | — | — | — | — | — | — | ✓ | — |
| tools | — | ✓ | — | — | — | — | — | — |
| zendesk-webhook | — | ✓ | — | — | — | — | — | — |
| elevenlabs-webhook | — | ✓ | — | — | — | — | — | — |
| channel-webhook | — | ✓ | — | — | — | — | — | — |
| sos-ingress | — | ✓ | ✓ | ✓ | — | — | ✓ | — |
| training-read | — | — | — | — | — | — | — | ✓ |
| training-write | — | — | — | — | — | — | — | ✓ |
| integration-retry | — | ✓ | — | ✓ | — | — | ✓ | — |
| crew-self | — | — | — | — | ✓ | — | — | — |
| sos-trigger | ✓ | — | ✓ | ✓ | ✓ | — | ✓ | — |

`authenticated` requires an authenticated principal in addition to role membership. `identity-public` exposes only sign-in/session state and CSRF tokens. An environment training bearer remains limited to training routes; an interactive training-client session can use identity endpoints to sign out. Service accounts additionally require the matching scope below, so a role inferred from one scope does not grant another permission. Signed webhook policies permit an unauthenticated bootstrap request only because handlers require the configured HMAC signature over the exact bounded body; cookie staff roles cannot bypass that check.

## Route groups and additional resource checks

| Routes | Policy / resource constraint |
|---|---|
| `/healthz`, `/health/live`, `/health/ready`, `/openapi.json`, public channel/AI/email status, Open311 services | public discovery; no operational case details |
| `/auth/status`, `/auth/login`, `/auth/demo-login`, `/auth/bootstrap-login`; `/auth/logout` | identity-public; authenticated logout; CSRF on login/logout |
| GET `/api/incidents/{id}`, resident agent sessions | resident-read; public case projection excludes report/contact/transcript/internal actors; authenticated crew may read only assigned work |
| POST `/api/incidents`, `/open311/v2/requests` | resident-write; tenant comes from principal or resident host/path |
| POST `/api/channels/elevenlabs/simulated-chat` | resident-write; report intake only, available only in simulated channel mode; cookie CSRF and municipality isolation apply |
| GET `/api/incidents`, `/api/dispatch/incidents/{id}` | incident-read; crew list/details filtered to their `civicops:crew` binding |
| GET `/api/crews`, `/api/work-order-types` | workforce-read; crew list limited to their bound crew |
| GET `/api/dispatcher` | staff-identity; own identity only |
| GET metrics, readiness, outbox, alerts, crew eligibility; `/api/stream` | operations-read; municipality partition; crew has no general operations stream |
| POST case approve, assign, comms approve, callback, demo seed | dispatch; high/critical catalogue hazard or Urgent priority approval/assignment additionally requires supervisor or admin |
| PATCH case status | work-status; crew only assigned work and `InProgress`, `WaitingForCitizen`, `Resolved` transitions; existing lifecycle/version checks remain |
| POST work-order-type override | supervise; clears approval and assignment, requires renewed review |
| GET audit, verify, evidence export, signed `/api/audit/export` and training audit/verify; POST `/api/audit/export/local` | audit; tenant-bound; privacy-filtered evidence export, signed audit download and exclusive local evidence files; cookie CSRF applies to local export |
| POST outbox retry | integration-retry; scoped service accounts need integrations:write |
| `/api/admin/*`, POST retention purge | admin; local tenant scope; default-tenant admin can provision new municipality metadata, cannot edit another municipality's operational records |
| ElevenLabs tool POST `/api/channels/elevenlabs/tools/{tool}` | tools; bounded authenticated agent tools cannot approve/assign |
| Signed Zendesk webhook / ElevenLabs post-call | respective webhook policy and required signature; source municipality claim or configured host/path, never a body field |
| GET `/api/v1/training/*` | training-read; catalogue/qualifications/SSE in current municipality |
| POST training completions/certifications | training-write; idempotency, eligibility and trusted-client supervisor attestation preserved |
| GET `/api/v1/crew/auth/config`; POST `/api/v1/crew/auth/challenge`, `/api/v1/crew/auth/oidc`, `/api/v1/crew/pairing/exchange` | Public/identity-public discovery and bounded exchange; issuer or one-use pairing validation establishes worker and tenant |
| `/api/v1/crew/me/*`, shift start/end, consent, locations and `POST /sos` | crew-self; current worker membership, device ownership, assigned work, version/idempotency and independent shift/consent checks. `POST /sos` records the shared control-room alert and accepts no coordinates |
| Crew pairing, device list/revocation, policy and retention purge; `/api/v1/crew/jobs/{id}` evidence review | supervise; current municipality only, named reason for grants/revocation/policy, audited data access |
| POST `/api/v1/sos` and location/cancel | sos-trigger; residents and crew may trigger or share only their own active SOS, with crew coordinates requiring an active shift, explicit consent and visible indicator |
| POST SOS acknowledgement/assessment/dispatch/resolve/false-alarm; GET queue/detail/SSE | dispatch or operations-read; named authorised human decisions, tenant-scoped queue and audited access; resident contact is a hash |
| POST SOS SMS/WhatsApp ingress and simulated USSD | sos-ingress; demo is synthetic, while configured deployments require the signed SOS ingress envelope; allow-listed keywords only and no provider send |

Auditor alone has read/export permissions and cannot mutate cases, workforce, credentials or privacy requests, including resident intake. The protected local export operation writes evidence artifacts only. Crew and auditor data cannot be retrieved by changing an id or body tenant. SSE subscriptions capture municipality; stream events contain only event names. OpenAPI publishes `x-civicops-policy`, `x-civicops-roles` and tenant resolution for every route. OIDC callback paths are processed by the authenticated ASP.NET handler, rather than mapped business endpoints. [Signed exports, signing-key requirements and local/cloud immutability limits](operations/AUDIT_EXPORT.md).

Crew-device credentials are a separate restricted principal: the permission matrix permits **only `crew-self` with `crew:self` scope**, even if another role were present. They cannot use legacy staff/work-status, dispatcher, map, admin or ordinary identity-session routes. Pairing grants expire after eight hours, native OIDC grants no later than the validated issuer token expiry, and supervisors can revoke either. Native issuer claims must match existing tenant/crew/worker membership; an explicit municipality-path mismatch fails before a grant is created. [Crew authentication and privacy contract](CREW_APP.md) records the separate native public-client configuration and live-issuer acceptance limits.

## Staff SSO

Set `CIVICOPS_DEMO_MODE=false` behind an HTTPS ingress. Leave the OIDC group entirely blank to disable SSO honestly. A partially configured group fails startup. Configure:

- `CIVICOPS_OIDC_PROVIDER`: `entra`, `google` or `generic`.
- `CIVICOPS_OIDC_CLIENT_ID` and `CIVICOPS_OIDC_CLIENT_SECRET`: private issuer application credentials.
- Generic: `CIVICOPS_OIDC_AUTHORITY` must be the absolute HTTPS issuer URL, with no credentials/query/fragment.
- Entra preset: `CIVICOPS_OIDC_DIRECTORY_ID` is a specific directory GUID, producing `https://login.microsoftonline.com/{directory}/v2.0`; multitenant `common`/`organizations` presets are rejected. An explicitly supplied authority must still pass validation.
- Google preset uses `https://accounts.google.com`. Set `CIVICOPS_OIDC_HOSTED_DOMAIN` to the reviewed Workspace domain to restrict the validated `hd` claim. Google does not supply CivicOps roles automatically; map trusted claims or use an issuer/directory configuration that does. The preset cannot invent group membership.
- `CIVICOPS_OIDC_ROLE_CLAIM` (default `roles`), `CIVICOPS_OIDC_GROUP_CLAIM` (default `groups`), `CIVICOPS_OIDC_TENANT_CLAIM` (default `municipality_id`). Map explicit issuer values with `CIVICOPS_OIDC_ROLE_MAP` / `CIVICOPS_OIDC_GROUP_MAP`: `MunicipalDispatch=dispatcher;MunicipalSupervisors=supervisor;MunicipalAudit=auditor`. Comma-separated local roles are supported. Unmapped users fail sign-in; issuer `roles` are never trusted as local roles without mapping.
- `CIVICOPS_OIDC_TENANT_ID` optionally pins all identities from that issuer to one existing municipality; otherwise exactly one valid mapped tenant claim is required. Tenant IDs and slugs use bounded lowercase letters/digits/hyphens. A crew role also needs a trusted `crew_id` to see assigned work.
- `CIVICOPS_OIDC_SESSION_MINUTES`: 5–1440, default 480. No sliding refresh or provider access-token persistence.

Register the HTTPS redirect `{origin}/signin-oidc` and sign-out callback `{origin}/signout-callback-oidc` with the owner’s issuer. The ASP.NET Core OIDC handler uses authorization code + S256 PKCE, nonce, correlation, discovery and JWKS signature validation. `__Host-CivicOps-Session` is Secure, HttpOnly, SameSite=Lax and contains an encrypted opaque reference; tickets/claims stay in the server session store. Sessions expire and are lost on process restart. SSO role/group membership is the validated issuer snapshot at sign-in until session expiry or re-sign-in; there is no directory polling or continuous-access evaluation. Entra group-overage references are not resolved via Graph, so the owner must configure complete group claims or application roles and issuer MFA/access policies. OIDC ID tokens are retained only in server tickets for issuer logout hints; access/refresh tokens are never persisted. Logout removes the local ticket and uses the issuer end-session endpoint when advertised, otherwise completes locally. Reverse-proxy TLS/forwarded-header configuration must be reviewed by the owner; the application does not blindly trust forwarded headers.

`/auth/status` issues a CSRF token. Send it as `X-CivicOps-CSRF` on cookie-authenticated mutations (a form `__RequestVerificationToken` also supports browser OIDC logout). Anonymous bootstrap/demo login requires CSRF too. Production CSRF cookies are Secure, HttpOnly and Lax. Only explicit demo mode allows the CSRF cookie over local HTTP to keep the one-command walkthrough working; the staff session cookie remains Secure in every mode. Role choice is visibly **EXPLICIT DEMO / synthetic / unverified**, and the demo sign-in endpoint returns 404 in non-demo mode. Unauthenticated explicit demo retains the prior synthetic supervisor workflow; it never grants admin.

## Non-SSO bootstrap users

For initial administrative access, generate a private random token, set only its SHA-256 hex hash in `CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH`, and optionally set `CIVICOPS_BOOTSTRAP_ADMIN_TENANT` (default `default`). Neither variable belongs in committed configuration. Supply the clear value only in the protected bootstrap sign-in form. This configuration token cannot be used as a service bearer. The server validates its hash in constant time and creates an admin session in the configured existing municipality. Rotate configuration and restart to invalidate those initial sessions. Blank configuration disables this path.

Administrators issue local users with subject, display name, role list and optional crew binding. Their random access tokens are shown once, stored only hashed and expire after 30 days. Role edits refresh existing sessions on the next request. Rotation and revocation invalidate existing user sessions; all changes append tenant audit evidence. Use a trusted municipal SSO directory for normal staff where available. `CIVICOPS_DISPATCHERS` and legacy `CIVICOPS_DISPATCH_TOKEN` remain default-tenant dispatcher+supervisor bootstrap service credentials for old workflows, without admin rights. Legacy training hashes remain separate and default-tenant bound.

## Service accounts

Create accounts in **Administration → Service accounts**, reviewed per municipality, with one or more explicit scopes and expiry within 366 days. The clear random token appears only in the create/rotate response; list/audit/snapshots never include it. Tokens authenticate as `Authorization: Bearer {token}` and bind the principal to the account’s municipality. No service scope grants staff decisions or admin access.

| Scope | Enabled operation |
|---|---|
| tools:invoke | ElevenLabs five agent tools; also signed ElevenLabs post-call when using service authentication |
| zendesk:webhook | Signed Zendesk return webhook; signature still required |
| training:read | Training catalogue, worker qualifications and training SSE |
| training:write | Idempotent training completion and controlled simulation certification grant |
| incidents:read | Tenant incident list/internal detail and public status |
| incidents:write | Resident-style intake only; no approval, assignment, override or status update |
| audit:read | Tenant audit/verification, privacy-filtered evidence export and signed audit download/local export |
| integrations:write | Tenant dead-letter outbox retry |

Rotation supports a 0–60 minute old-token overlap capped by its original expiry. A second rotation replaces the previous overlap token; revocation immediately disables both tokens. Expiry/overlap/revocation are checked on each request. Changes have per-tenant identity audit chains mirrored recoverably into that municipality’s incident chain. Service account and bootstrap-user records persist in the selected backend and filter tenant access; operational stores use distinct tenant partitions.

[ElevenLabs service-account setup](ELEVENLABS_SETUP.md). Provider tool templates use an Authorization workspace-secret locator; the workspace secret value must include the `Bearer ` prefix. The legacy `CIVICOPS_ELEVENLABS_TOOL_SECRET`/`X-CivicOps-Tool-Secret` still works only as a default-tenant bootstrap. New provider credentials, live token rotation/delivery and separate per-tenant provider account deployments remain owner validation.

## Tenant data and administration

A municipality stores id, name, slug, departments, South African language/time-zone defaults, service-area description and optional unique resident hostname. Explicit demo seeds `default` (inland) and `synthetic-coast` (coast), with synthetic crews/workers in each. Select resident intake through `/m/inland/` or `/m/coast/` or a configured `residentHost`. Authenticated requests use only `civicops:tenant`; attempting to use another municipality’s host/path returns 403. Unknown resident slugs return 404. A tenant field in a body never changes the binding.

Incidents, workers, crews, training records, audit chains, integration deliveries and voice receipts are partitioned. Legacy on-disk records load only into `default`, preserving historical hashes and adding migration evidence; copying legacy files into another partition fails closed. Tenant audit verification does not expose another tenant’s sequence/hash. Intake, dispatch, tools and training rate partitions include the municipality so one municipality cannot consume another municipality’s quota. The municipality directory intentionally exposes shared routing metadata to administrators, and synthetic names/slugs to the demo picker; it carries no resident/workforce/credential data.

Admin catalogue edits reject duplicate/unknown JSON keys and enforce the portable `docs/schemas/work-order-catalog.schema.json` field bounds, required values, enums and catalogue references. The submitted version must increase and `expectedVersion` must match the current tenant revision. Stable classifier codes cannot be removed; new work orders/services are allowed, and category/department mappings must remain coherent. Immutable hash-chained revisions survive restart, recover a missing global audit projection and fail closed on tampering. The active request catalogue is used by intake, service discovery, eligibility and training. Worker/crew/certification records are validated and audited; synthetic seed certificates remain visibly synthetic. Legacy grants still require a trusted-client supervisor attestation, not a verified individual external certification authority.

## Tested boundaries and owner work

Implemented tests include the real OIDC handler with an in-process discovery/JWKS/token issuer, PKCE verification, role/tenant mapping, sign-in/access/sign-out, claim/config rejection, server ticket expiry, service expiry/overlap/revocation, bootstrap session role refresh, per-tenant storage/migration/audit verification, cross-tenant lifecycle/tools/Open311/signed webhooks/SSE and admin mutations. Browser checks exercise keyboard access, CSRF, non-admin denial, token lifecycle, role/municipality changes, catalogue validation and mobile layout. `docs/media/admin-console.png` is an actual synthetic browser capture.

PostgreSQL persistence, file-to-database import, both-backend backup/restore drills and signed local audit exports are implemented and locally tested. Documented-only/owner-pending: real issuer application registrations and group claims, live federated logout behavior, production TLS/ingress, provider account setup and delivery, actual AMD inference, remote CI and container runtime, production database throughput, distributed sessions/quotas/SSE, resident identity, externally anchored/cloud-WORM audits, production backup scheduling/retention and approved recovery objectives, and complete privacy erasure. Delivery remains at least once with downstream deduplication/reconciliation; the coarse database lock does not establish production scaling. See [backup/restore](operations/BACKUP_RESTORE.md) and [audit export](operations/AUDIT_EXPORT.md). The test issuer and offline provider transports do not prove production infrastructure readiness.
