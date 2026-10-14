# Onboard a South African municipality

Reviewed **8 October 2026 (SAST, UTC+02:00)**. The admin APIs/UI, tenant isolation, OIDC handler and qualification checks are implemented and locally tested with synthetic records/fake issuers. This is the **owner adoption checklist**, not evidence that a municipality has gone live. Account registrations, provider writes, production deployment, legal approval and real staff/data import remain owner-pending. Keep completed checklists and real records in protected municipal systems outside this repository.

## 1. Assign accountable owners and approve scope

Name the municipal service owner, registered Information Officer/deputy, department supervisors, hosting/database operator, identity administrator and support responders. Agree service area, resident alternatives/emergency route, initial channels, staffing/volume and covered hours. Complete [DPIA_TEMPLATE.md](privacy/DPIA_TEMPLATE.md), approve lawful basis/notice/operator contracts/transfers and record risks that block pilot. Adopt the [runbook](operations/RUNBOOK.md), [incident procedure](operations/INCIDENT_RESPONSE.md) and an actually staffed [support schedule](SLA_SUPPORT.md). Backend redaction does not prevent upstream ElevenLabs speech processing or erase historical/provider/backup copies.

OIDC/provider credentials, Information Officer contact and retention settings are process-wide. There is no per-municipality provider/issuer/retention configuration screen. If municipalities require different issuer accounts, provider accounts or retention/Officer policies, use owner-approved separate deployments or implement/test that extension first. Tenant partitioning alone does not establish these policies.

## 2. Prepare an isolated installation and initial administrator

Follow [DEPLOYMENT.md](operations/DEPLOYMENT.md): immutable scanned image, one active instance, private PostgreSQL/data root, backups, TLS/allowed hosts/trusted proxy, narrow network access and monitoring. Static assets have passed local validation; Docker/Kubernetes runtime and actual ingress/SSO are unverified. Use only synthetic reports for acceptance until sign-off. The blank [.env.example](../.env.example) is an inventory, not a loaded configuration file.

Set `CIVICOPS_DEMO_MODE=false`. A protected bootstrap-admin token is generated privately by the owner; configure only its SHA-256 through `CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH` or `_FILE`, bound initially to `default`. Sign in through Staff access → bootstrap access token over HTTPS. Blank bootstrap configuration disables that path. Use [RBAC.md](RBAC.md) for exact setup; no real token or filled env file belongs in the checkout.

## 3. Create and verify the municipality boundary

While signed in as **default-tenant admin**, use Administration → Municipalities → provision municipality, or `POST /api/admin/tenants`. Only default-tenant admin can create a municipality. Use a stable lowercase id/slug, unique optional DNS `residentHost`, approved departments/service area and `Africa/Johannesburg` time zone. This exact **synthetic** payload illustrates the fields:

```json
{
  "id": "synthetic-pilot",
  "name": "Synthetic Pilot Municipality",
  "slug": "synthetic-pilot",
  "departments": ["Water and Sanitation", "Roads and Stormwater"],
  "localeDefaults": { "language": "en", "timeZone": "Africa/Johannesburg" },
  "serviceArea": "Fictitious test area; synthetic acceptance only.",
  "synthetic": true,
  "residentHost": null,
  "version": 1
}
```

Tenant creation does not create its staff admin. Configure the approved issuer's municipality binding, or during a planned restart rebind the private bootstrap administrator via `CIVICOPS_BOOTSTRAP_ADMIN_TENANT` to this **existing** tenant. Sign in to that municipality and create its approved local admin/users if needed. An administrator in `default` cannot operate another tenant's cases/workforce merely by editing an id. Remove/rotate initial bootstrap access once normal administration is proven.

Update only the current municipality with `PUT /api/admin/tenants/{id}` using its latest directory `version`; id is fixed. Confirm unauthenticated resident routing at `/m/{slug}/` or the approved hostname, authenticated tenant binding, and denial of another municipality's host/path. Body tenant fields do not change binding. Tenant locale metadata supports South African language codes, but resident UI translations currently cover **English/Afrikaans/isiZulu only**; metadata does not add translations.

Source: [TenantStore.cs](../products/civicops/Services/TenantStore.cs), [AdminEndpoints.cs](../products/civicops/Services/AdminEndpoints.cs), [tenant tests](../tests/CivicOps.Tests/TenantIsolationTests.cs).

## 4. Configure SSO and least-privilege roles

One process uses one configured OIDC handler/issuer. Leave the whole group blank to disable SSO; partial configuration fails startup. All setup below is performed by the authorized municipal identity administrator in an existing account. No account or identity-provider write is performed by the repository's tests.

**Entra ID:** register a confidential **Web** application for the municipality's specific directory. Register the exact HTTPS redirect `{origin}/signin-oidc` and supported logout return `{origin}/signout-callback-oidc`. Use the directory GUID/client id and privately managed client secret with `CIVICOPS_OIDC_PROVIDER=entra`, `CIVICOPS_OIDC_DIRECTORY_ID`, `CIVICOPS_OIDC_CLIENT_ID`, `CIVICOPS_OIDC_CLIENT_SECRET_FILE`. Configure municipal app roles or complete group claims, explicit external-value mappings and issuer MFA/access policy. The preset rejects `common`/`organizations`; Graph resolution of group-overage claims is not implemented. [Microsoft redirect-URI configuration](https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-redirect-uri). Current CivicOps uses client-secret authentication only; certificate/private-key and federated client authentication are not implemented. [Microsoft production credentials guidance](https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials) recommends stronger credentials and discourages production client secrets. The municipal identity/security owner must resolve that policy gap through an approved exception or a tested authentication extension before acceptance.

**Google Workspace:** use an existing municipality-controlled Cloud project and confidential **Web application** OAuth client, review branding/consent and register `{origin}/signin-oidc`. Set `CIVICOPS_OIDC_PROVIDER=google`, client id/private secret and `CIVICOPS_OIDC_HOSTED_DOMAIN` to the reviewed Workspace domain. The app checks the validated `hd` claim, not an email suffix alone. Google OIDC does not supply CivicOps roles or directory groups automatically. Choose a reviewed trusted claim mapping, for example an explicit allowlist of verified `sub` values via `CIVICOPS_OIDC_ROLE_CLAIM=sub` and `CIVICOPS_OIDC_ROLE_MAP`, or use a municipal broker/generic issuer supplying authorized role claims. Manage the allowlist in restricted owner configuration and review joiner/leaver changes; no Workspace directory sync is implemented. Broadly mapping every domain user to an admin role is unsafe. Google logout can complete locally when no end-session endpoint exists; test the actual account/session behavior. [Google OpenID Connect setup and claims](https://developers.google.com/identity/openid-connect/openid-connect).

For either issuer, pin `CIVICOPS_OIDC_TENANT_ID` to the existing municipality for a single-municipality installation, or require exactly one trusted `municipality_id` claim with `CIVICOPS_OIDC_TENANT_CLAIM`. Explicitly map reviewed external values using `CIVICOPS_OIDC_ROLE_MAP` / `CIVICOPS_OIDC_GROUP_MAP` (`external-value=dispatcher;other-value=auditor`). An unmapped user cannot sign in. Crew access additionally requires one trusted `crew_id` for that tenant. Set the approved `CIVICOPS_OIDC_SESSION_MINUTES` (5–1440; default 480); roles reflect sign-in claims until session expiry/re-sign-in. Restart clears server sessions/CSRF state. Actual provider logout/revocation/HTTPS remains owner validation.

Assign roles using the full [permission matrix](RBAC.md): dispatcher for ordinary review/assignment, supervisor for high/critical hazards and overrides, crew for assigned work/status only, auditor for read/export and admin for provisioning/privacy/credentials. Service accounts have scoped noninteractive access and cannot approve or assign. Separate human decision makers and integration clients. Verify each intended role and an unmapped identity, wrong tenant, unassigned crew and unauthorized admin mutation through the real ingress. Local users issued in Administration expire after 30 days and are an approved fallback, not automatic SSO membership.

Evidence: [IdentityOptions.cs](../products/civicops/Services/IdentityOptions.cs), [OIDC integration tests](../tests/CivicOps.Tests/OidcIntegrationTests.cs), [IdentityTests.cs](../tests/CivicOps.Tests/IdentityTests.cs).

## 5. Approve departments and import the service catalogue

Export the current tenant catalogue via Administration or `GET /api/admin/catalogue`, keep its `expectedVersion` and prepare the complete JSON according to [work-order catalogue schema](schemas/work-order-catalog.schema.json). Align municipality department names, work-order/category/service-code mappings, hazards, skill levels, issuing basis, PPE/procedures and review targets with department supervisors. Existing stable classifier codes cannot be removed; new supported codes/services may be added. Advance semantic catalogue version and submit through Administration → Work-order catalogue or `PUT /api/admin/catalogue` with `{expectedVersion, catalogue}`. A conflict requires refresh/review; never overwrite it blindly.

This is a validated **JSON catalogue replacement**, not a CSV importer. If the municipal source is CSV, privately map/review it into the schema first; no bundled converter or automatic department import exists. Validate in the synthetic staging tenant and record source/version/reviewer/counts. Review targets are human review policy, not promised arrival. Discover the resulting services and crew eligibility, confirm training version/ETag and verify audit after acceptance. See [CatalogueAdminStore.cs](../products/civicops/Services/CatalogueAdminStore.cs) and [admin tests](../tests/CivicOps.Tests/AdminConsoleTests.cs).

## 6. Prepare crews, workers and certificate CSV records

**No CSV upload/importer, deletion endpoint or atomic workforce bulk import is implemented.** A municipality may stage approved CSV records privately, then have an admin map each reviewed record to the existing JSON editors/POST/PUT API. A custom importer, reconciliation report and bulk rollback require separate implementation/acceptance; they are not delivered here.

| Suggested private source CSV | Fields mapped to current API |
|---|---|
| `crews.csv` | `id,name,department,capability,shift,availability`; JSON `memberIds` is an array |
| `workers.csv` | `id,displayName,crewId`; join skills/certificates by municipality-local worker id |
| `skills.csv` | `workerId,code,level` → worker `skills` array; level 1–5 and existing catalogue code |
| `certifications.csv` | `workerId,code,issuedAt,expiresAt,issuer,source,evidenceRef` → worker `certifications` array; ISO timestamps, matching catalogue issuing basis and approved validity |

Use protected evidence references, not ID numbers, certificate scans or real documents in this repository. Code validates fields/dates/catalogue basis; it does not contact an accreditation registry or prove assessor identity. Authorized municipal staff must independently verify external certificate authenticity and legal scope. A passing training completion alone does not grant a certificate; the controlled endpoint supports applicable simulation certificates only with trusted-client supervisor attestation. [Training boundary](EXTENSIONS.md).

Import order through tenant-admin sessions with CSRF:

1. Review and snapshot current workforce/catalogue, approve mappings and duplicate ids. New tenants automatically have **synthetic seed crews/workers, including outside demo mode**. Mark seed crews `unavailable`; do not use synthetic qualifications for a real assignment. There is no seed-deletion command.
2. Create approved crews via Administration → Crews / `POST /api/admin/crews`, initially `availability:"unavailable"` and `memberIds:[]`. Department must exactly match the active catalogue. New crew ids cannot replace existing records by POST; use the edit/PUT flow after review.
3. Create workers via Administration → Workers & certifications / `POST /api/admin/workers` with an existing crew, reviewed `skills` and `certifications`. The server adds worker membership to the correct crew. `PUT /api/admin/workers/{id}` edits an existing worker; moving crew updates membership.
4. Reconcile each returned record and source row/count, retaining failed rows/decisions. There is no bulk transaction or expected-version workforce edit: coordinate one importer/admin at a time and review before retries. Worker PUT is a whole reviewed record; do not accidentally drop an existing skill/certificate.
5. Verify dates/source/evidence, department/availability/skills and an expired/missing-certificate negative case. Set `CIVICOPS_CERT_POLICY=block` (non-demo default). Activate only approved real crews through `PUT /api/admin/crews/{id}`. Confirm seed crews remain unavailable and assignments are explicitly human-approved.

Evidence: [workforce models](../products/civicops/Models/Workforce.cs), [WorkforceStore.cs](../products/civicops/Services/WorkforceStore.cs), [CrewEligibilityService.cs](../products/civicops/Services/CrewEligibilityService.cs), [qualification tests](../tests/CivicOps.Tests/WorkforceExtensionTests.cs).

## 7. Configure ElevenLabs channels and Zendesk

Keep provider simulation in the isolated synthetic installation until the owner approves processor terms, channel notices, accounts, secret permissions and cross-border/retention policy. Follow the exact numbered [ElevenLabs owner checklist](ELEVENLABS_SETUP.md) and [Zendesk checklist](ZENDESK_SETUP.md); all live writes/account changes remain owner work.

For ElevenLabs, create a tenant-bound CivicOps `tools:invoke` service account, securely store its once-shown bearer in the existing workspace secret, review the dry-run agents/tools/knowledge and provision only with explicit owner authorization. Configure private signed-URL/WebRTC session mode or reviewed public widget allowed hosts; set agent/webhook ids/secrets. A phone number/agent requires an existing approved telephony account, provider number id/agent attachment and synthetic inbound/callback/transfer checks. Callback consent is separate and no arrival promise is made. Live microphone/network, phone routing and actual consent/language quality are unverified here. English/Afrikaans conversational presets exist; live isiZulu voice remains pending, with form access available.

For Zendesk, configure the existing account's supported OAuth credential, reviewed return webhook/HMAC secret/trigger mapping and tenant-scoped service credential where used. OAuth refresh is not implemented. Review private ticket notes versus explicitly approved public updates and source/event-id deduplication. The outbox is durable and at least once, with bounded reconciliation/backoff/dead letters; provider search consistency can still permit duplicates. Test one synthetic ticket, reply, approved update, status/restart recovery and ambiguous/uncertain response handling in the account before resident use. Do not remove credentials or enable demo on a live pending queue to bypass an outage.

The provisioner defaults to an offline redacted dry run. These optional provider checks are **read-only but still owner-operated/account-dependent**, never automatic startup/health checks:

```bash
dotnet run --project products/civicops -- elevenlabs provision
dotnet run --project products/civicops -- elevenlabs provision --check
dotnet run --project products/civicops -- zendesk --check
```

Blank settings remain honestly simulated/disabled; connectivity checks do not prove write permissions or correct webhook behavior. Automated verification never contacts live provider APIs.

## 8. Register privacy ownership and choose retention

The Information Officer/deputy confirms registration, approved resident notice/PAIA manual, lawful basis, identity-verification procedure, access/correction/objection channels and operator agreements. Set the approved `CIVICOPS_INFORMATION_OFFICER_CONTACT`; the API label remains `configured-unverified`, because configuration is not registration. Current purpose notice text/version is code-backed; changing the municipal purpose requires a reviewed/versioned policy/resource change, not an admin free-text setting. Machine-drafted Afrikaans/isiZulu need native-speaker and municipal notice review.

Choose approved `CIVICOPS_RETENTION_DAYS` and optionally shorter `CIVICOPS_PII_RETENTION_DAYS`, values 1–36500; blank/invalid settings disable that schedule. Closed-case milestones drive hourly purges; pending/dead-letter deliveries protect cases. No automated per-record legal hold exists, so resolve statutory record/hold requirements before enabling purge. Test the closed-case and pending-delivery boundaries, privacy-request verification/export/correction/local anonymisation and separate provider/backup/audit retention procedure. Local anonymisation leaves workflow/receipt/tombstone evidence and does not erase historic audit, provider records, backups or prior exports. Different tenant retention schedules require separate deployment or new tested configuration. [POPIA implementation/limits](privacy/POPIA.md).

## 9. Train operators and prove continuity

Use synthetic data in a separate staging installation. Train resident-service staff to explain purpose/contact choice and alternatives; dispatchers to assess unverified location, urgency/fallback rationale and duplicates; supervisors to review hazards/overrides/certificates; crews to update assigned work; auditors to verify/export evidence; admins to manage least privilege, one-time tokens and externally verified privacy requests. Explain simulated/live/unverified labels and catalogue targets without promising crew arrival.

Run at least one human workflow, denied unsafe assignment, outbox outage/dead-letter/retry exercise, staff leaver/credential rotation and tenant-isolation exercise. Hosting operators perform a measured restore and the full incident tabletop, including IO notification worksheet and offline comms review. The future training-layer API is an implemented extension contract, not a delivered VR curriculum, certification authority or verified supervisor sign-off system. Record attendance/competency and refresher dates privately.

## 10. Go-live acceptance and review

All items require named owner, evidence date and sign-off in the municipal acceptance record. An unchecked item is pending; synthetic test results do not waive it.

- [ ] Scope, Information Officer/registration, DPIA, lawful basis/notices, language/accessibility review and operator/transfer agreements approved.
- [ ] Immutable scanned image, one active instance, database grants/TLS/encryption, mounts/network/ingress/proxy and real HTTPS SSO/roles/logout accepted; bootstrap access controlled.
- [ ] Tenant routing and cross-tenant/role/crew denials verified on the deployed installation.
- [ ] Catalogue/hazards/procedures/review targets approved; seed crews unavailable; actual workforce and certificates independently validated; `block` policy checked.
- [ ] Selected live channels/provider permissions/webhooks/phone and reviewed-update delivery tested with synthetic data; unconfigured modes explicitly accepted where appropriate.
- [ ] Retention/holds/provider/backup/export handling and verified resident-request procedure approved; no full-erasure claim.
- [ ] Fresh probes/collector dashboards and alert receiver working; signed SLO/support coverage and escalation/deputies tested.
- [ ] Encrypted/off-site backup custody and actual restore RPO/RTO measured; audit chains/exports verified; rollback/continuity and breach tabletop exercised.
- [ ] Staff training, resident communications and emergency alternatives ready; manual assistive-technology/native-language review complete.
- [ ] Service owner, security/hosting owner, department supervisors and Information Officer authorize the limited pilot, rollback triggers and review dates.

Review after the first operating week and monthly thereafter: intake failures/fallback/latency, pending/live delivery age, human review breaches, certificate expiry, resident requests, access/leaver changes, fresh audit evidence and recovery tests. Expand only on measured evidence. Local implementation evidence and remaining owner work are consolidated in [ENTERPRISE_READINESS.md](ENTERPRISE_READINESS.md).
