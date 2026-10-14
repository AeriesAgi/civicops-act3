# CivicOps HTTP API

JSON uses camelCase and string status enums, except the Open311 adapter and the provider-shaped ElevenLabs payloads. Normal write bodies are limited to 32 KiB, Zendesk webhooks to 64 KiB and ElevenLabs post-call events to 512 KiB. Validation and quota failures return ProblemDetails (with compatible error/message aliases and conflict snapshots). Invalid fields return 400, authentication failures 401, conversation mutation mismatch 403, absent resources 404, version/idempotency conflicts 409, oversized bodies 413, exhausted quotas 429 and missing required integration configuration 503.

## Dispatcher access and public projections

Outside explicit demo mode, protected routes require an authorized staff session or `Authorization: Bearer <scoped service/bootstrap token>`. Every mapped route has an explicit policy; see [RBAC and enterprise identity](../../../docs/RBAC.md). Staff browser writes require `X-CivicOps-CSRF` from `/auth/status`. Configure verified identities as semicolon-separated `name:SHA256(token)` pairs in `CIVICOPS_DISPATCHERS`. `CIVICOPS_DISPATCH_TOKEN` is the legacy single-identity alternative. Audit actors come from verified configuration; a supplied `approvedBy` label never overrides them. Demo actor is `Demo supervisor (not signed in, unverified)`.

Authentication occurs before the dispatch quota is charged. Dispatch partitions use authenticated subjects/token hashes and all quotas include municipality; anonymous intake and invalid credential attempts use a separate transport-IP partition. A rejected credential attempt can therefore return 429 after its anonymous quota is exhausted, without spending a verified dispatcher's quota. Defaults are 120 dispatch operations and 60 intake operations per minute, adjustable with `CIVICOPS_DISPATCH_LIMIT` and `CIVICOPS_INTAKE_LIMIT`. Authentication routes use a separate tenant/principal/IP budget (20 by default, `CIVICOPS_AUTH_LIMIT`). Tools add a shared `CIVICOPS_TOOL_LIMIT` cap (120 by default) to conversation limits. Invalid training credentials have a separate auth budget and cannot consume a verified trainer's quota. SSE opens consume a dispatch permit; refresh events contain no case data.

`GET /api/incidents/{id-or-reference}` is the minimal public projection: reference, ids/version/timestamps, category/department/priority/status, a safe status message and review/location-verification flags. It omits contact, description, area, raw transcripts, approvals, internal notes and agent output. `GET /api/incidents` and `GET /api/dispatch/incidents/{id-or-reference}` require dispatcher access and return the operational queue/detail.

## Resident intake

`POST /api/incidents` accepts:

```json
{
  "description": "Synthetic burst pipe leaking near the demonstration hall.",
  "area": "Synthetic Ward",
  "contactEmail": "resident@example.invalid",
  "residentLanguage": "en",
  "contactPreference": "email",
  "consentToContact": true,
  "callbackConsent": false,
  "privacyConsentAccepted": true,
  "privacyConsentVersion": "civicops-popia-v1"
}
```

Production intake requires explicit acceptance of the exact current text from `GET /api/privacy/notice`. Contact consent is a separate optional choice; no identity document is needed for intake. New cases record notice version, purpose, text, time and an unverified-resident assertion. Demo/internal legacy requests missing consent remain labelled unverified. See [POPIA](../../../docs/privacy/POPIA.md).

Send `Idempotency-Key` and retain it across uncertain retries. Creation returns 201 with `{ "incident": <public projection>, "replayed": false }`; an identical retry returns 200/replayed true without another case or integration intent. Reusing a key for different normalized content returns 409. The form adapter fixes source/label to web/Resident form; clients cannot impersonate the voice channel by supplying source labels.

All new cases start `PendingApproval` with `needsHumanReview=true`. Intake, triage, duplicate, dispatch recommendation and comms draft runs are persisted. Open-model failures produce recorded deterministic fallback. Danger language floors urgency. `ContactPreference` is descriptive and does not grant consent. Unconsented email/phone values are discarded before case persistence.

| Field | Bound |
|---|---:|
| description | 10–4,000 characters |
| area; locationProvenance | 200 |
| contactEmail | 254; valid address; no CR/LF |
| source; serviceCode; contactPhone | 40 |
| sourceLabel | 80 |
| residentLanguage | 12; en/af allowlist |
| contactPreference | 20; none/email/voice allowlist |
| conversationId | 128 |
| privacyConsentAccepted | true required in production |
| privacyConsentVersion | exact current version from notice |
| Idempotency-Key | 200 |
| action note/publicUpdate/approvalNote | 2,000 |
| caller approvedBy label | 80; identity still server verified |

## Human workflow and comms

`POST /api/incidents/{id}/approve`:

```json
{ "approvedBy": "Synthetic caller label", "note": "Location checked for demonstration.", "expectedVersion": 1 }
```

`POST /api/incidents/{id}/assign`:

```json
{ "crewId": "water-01", "approvedBy": "Synthetic caller label", "expectedVersion": 2 }
```

`PATCH /api/incidents/{id}/status`:

```json
{ "status": "InProgress", "approvedBy": "Synthetic caller label", "expectedVersion": 3 }
```

Successful mutations return `{ "incident": <dispatcher projection> }`. `expectedVersion` is optional for API compatibility, but clients should send the most recently fetched positive version; any explicitly supplied stale version returns 409 with the current projection. Invalid transitions, missing approval or an ineligible crew return 400. Use the approval and assignment routes for those operations. `publicUpdate` on the status request is an internal audited note; the public message is generated from the safe status template.

Workflow comms drafting runs outside persistence locks. When `expectedVersion` is omitted, the final atomic write may preserve concurrent Zendesk delivery records and ticket metadata if every other case field and the existing audit prefix are unchanged. Concurrent human decisions, safety/review changes, consent changes or location changes during drafting still return 409; the request must be reviewed again.

States are New, Triaged, PendingApproval, Approved, Assigned, InProgress, WaitingForCitizen, Resolved and Closed. New reports use PendingApproval. InProgress requires recorded human approval, no pending review and an eligible assigned crew. Resolved can reopen to InProgress; Closed has no next transition.

If new resident information or an upstream reply flags an Approved/Assigned case, the approval endpoint records a fresh human review while preserving its current status and assigned crew. Use the current version when approving again.

`GET /api/crews` returns the current municipality workforce capability catalogue; crew principals see only their bound crew. `GET /api/dispatcher` reports verified identity and explicit demo mode.

`POST /api/incidents/{id}/comms/{draftId}/approve` accepts `{ "expectedVersion": <current version> }`. It records the dispatcher, enqueues that exact approved draft as a Zendesk public comment, and writes an email only in outbox mode with recorded contact consent. Status changes supersede earlier pending drafts; the API refuses stale-status draft approval. Merely changing status or drafting a message never publishes an unapproved resident draft.

`POST /api/incidents/{id}/callback` requires an Idempotency-Key, dispatcher authentication, contact/callback consent, a contact phone and `ELEVENLABS_AGENT_PHONE_NUMBER_ID`. Live mode also requires the callback agent id. The request has no caller-controlled dispatch operation. Simulated response explicitly says no call was placed. Intent is recorded before a live API request; ambiguous failures suppress automatic retry and require provider reconciliation.

## ElevenLabs tools, sessions and post-call events

`POST /api/channels/elevenlabs/simulated-chat` is resident report intake when the channel is explicitly simulated. It accepts the `report_incident` payload below and preserves conversation/turn idempotency. It returns 404 in live mode before any provider request. Signed-in residents use their tenant and CSRF token; simulated reference lookup uses the minimal public `GET /api/incidents/{id}` projection. This route cannot invoke the other agent tools.

`POST /api/channels/elevenlabs/tools/{tool}` supports `report_incident`, `lookup_case_status`, `add_case_information`, `request_human_handoff` and `list_service_categories`.

Outside demo mode send `Authorization: Bearer <municipality service token>` with `tools:invoke`. The legacy `X-CivicOps-Tool-Secret` matching `CIVICOPS_ELEVENLABS_TOOL_SECRET` remains a default-tenant bootstrap option. Without either configured authentication route the legacy tool endpoint returns 503. New provisioned tools resolve the full bearer header value through an ElevenLabs workspace-secret reference; no clear token appears inline in agent JSON. Send bounded `conversation_id` and `turn_id` strings (128 max), except list_service_categories which needs neither.

Example synthetic report tool:

```json
{
  "conversation_id": "simulated-conversation-001",
  "turn_id": "report-turn-001",
  "description": "Synthetic blocked stormwater drain near the demonstration hall.",
  "area": "Synthetic Ward",
  "language": "af",
  "contact_preference": "none",
  "consent_to_contact": false,
  "callback_consent": false,
  "channel": "phone"
}
```

The short speakable response includes referenceNumber, status, nextStep, replayed and mode. Report idempotency uses conversation+turn+tool. Lookup accepts `reference_number` and returns minimal status. Additional-information/handoff tools accept `information` or `reason`, at most 2,000 characters, require the original conversation and append an event requiring review. They never dispatch a crew. Tool fields must have the declared scalar types; the body is bounded.

`GET /api/channels/elevenlabs/session` returns simulated mode without a key, `{mode, agentId, sessionMode:"public"}` for the configured public widget, or the provider's `signed_url`/`token` for signed-url/WebRTC mode. The API uses `Cache-Control: no-store`; private browser session behavior needs grant validation.

`POST /api/channels/elevenlabs/post-call` requires `ELEVENLABS_WEBHOOK_SECRET` and `ElevenLabs-Signature: t=<unix>,v0=<hex>`. Calculate HMAC-SHA256 over UTF-8 `<t>.<exact raw body>` with the signing secret. The SDK's 30-minute freshness tolerance and constant-time check apply. Do not reserialize a body between signing and sending.

Accepted types are `post_call_transcription`, `call_initiation_failure` and `post_call_audio`; `data.conversation_id` is required. Transcription attaches bounded transcript/summary/collection metadata to the linked case, including boolean `human_handoff_requested` values (direct or provider-wrapped). A requested handoff requires review even when the case was already approved. A collected description without a prior report tool can create a PendingApproval review case. Resident hazard language in follow-up keeps Urgent priority and review; no webhook approves dispatch. Initiation failures append case/system audit and a dispatcher alert visible in the control room. Audio is discarded. Event idempotency uses type+conversation; identical replay succeeds, different content for that event conflicts.

`GET /api/channels/elevenlabs/alerts` requires dispatcher access. `/api/channels/elevenlabs/status` and `/api/channels/status` expose live/simulated/disabled channel settings without secrets.

## Zendesk and delivery queue

`POST /api/channels/zendesk/webhook` expects `X-Zendesk-Webhook-Signature` and `X-Zendesk-Webhook-Signature-Timestamp`. The signature is base64 HMAC-SHA256 of exact timestamp bytes followed by exact body bytes; freshness is five minutes. The runtime always requires `ZENDESK_WEBHOOK_SECRET`.

```json
{
  "eventId": "synthetic-ticket-event-001",
  "caseReference": "CIV-SYNTHETIC-0001",
  "ticketId": 123,
  "status": "open",
  "comment": "Synthetic agent reply requiring dispatcher review.",
  "source": "zendesk"
}
```

Replies and upstream status changes append audit and flag review; they do not change the CivicOps dispatch lifecycle. Stable event ids suppress replay. `source:"civicops"` and CivicOps comment markers trigger the loop guard. The owner must map this application schema in the existing account's trigger; native event envelopes are not automatically interchangeable.

`GET /api/integrations/outbox` requires dispatcher access and returns metadata, attempts, next attempt, safe error, state and ticket ids, excluding raw JSON. `POST /api/integrations/outbox/{id}/retry` requeues a dead letter. `GET /api/channels/zendesk/status` returns safe aggregate delivery counts/mode. See [Zendesk setup](../../../docs/ZENDESK_SETUP.md) for exact authentication and remote reconciliation limits.

## Open311, audit, AI and demo

`GET /open311/v2/services` returns WATER, ROADS, ELECTRICITY, WASTE and GENERAL discovery entries. `POST /open311/v2/requests` accepts a JSON object with service_code, description, address_string, email and location_provenance; serviceCode/addressString/locationProvenance aliases are accepted. Codes match case-insensitively and unknown codes return 400. Non-object JSON and non-string fields are rejected. Service code supplies category/department alongside text classification, with conflicts requiring review. Idempotency-Key works here too. The result is an array with service_request_id, open status, status_notes and service_code. This is a bounded subset, not full Open311 conformance.

`GET /api/audit` and `/api/audit/verify` require supervisor, auditor or admin, or an audit:read service account. Verification returns valid, entries, failedSequence and message, and checks retained entries plus each case's latest state hash. Detected tampering blocks further store mutations. The chain is unanchored and is not independent proof against a full storage rewrite.

`GET /api/audit/export` has the same audit policy and returns a ZIP containing `audit.jsonl` and `manifest.json`. The manifest authenticates tenant, sequence/counts, chain head, byte length, SHA-256, timestamp and key ID with HMAC-SHA256. `CIVICOPS_AUDIT_SIGNING_KEY` or `_FILE` must supply at least 32 UTF-8 bytes through the secret provider; missing keys return an honest disabled 503. Optional `CIVICOPS_AUDIT_SIGNING_KEY_ID` labels the custody/rotation version. `POST /api/audit/export/local` requires the audit policy and cookie CSRF, and writes a new exclusive directory under absolute `CIVICOPS_AUDIT_EXPORT_DIR`; existing artifacts are never replaced. Downloaded audit history can contain historical resident information: restrict access and retention. `scripts/audit_verify.sh DIRECTORY` verifies the signature and full chain without starting the server or contacting storage/providers. Local read-only files provide application-level append-only output; cloud WORM/external anchoring is documented deployment work. [Format, CLI and immutability limits](../../../docs/operations/AUDIT_EXPORT.md).

`GET /api/evidence/export` requires supervisor, auditor or admin, or an audit:read service account and returns a minimal evidence projection with case status and AI provider/model/input-hash/confidence records, excluding descriptions, contact and raw transcripts. This is suitable for reviewing synthetic judge evidence; it is not a private case-data export.

`GET /api/ai/status` returns mode, model, providerLabel, safe endpointLabel, callCount/httpRequestCount/fallbackCount, p50LatencyMs/p95LatencyMs, fallbackRate, latencyWindowSize, timeoutMs and maxAttempts. Metrics are process-local; lifetime counts reset on restart and latency percentiles retain the latest 512 decisions. Hardware is operator-declared, not attested. `GET /api/ai/triage-contract` exports the production prompt/schema for the synthetic benchmark, with no operational case data.

`GET /api/stream` is an authenticated SSE refresh stream; use authorized fetch rather than putting a token in the URL. Poll if streaming fails. API/Open311 operational data remains network-only under the service worker.

`POST /api/demo/seed` creates/replays 15 synthetic cases only when demo mode is enabled; otherwise returns 404. `/healthz` reports the service, email mode, demo flag and selected data boundary. `/api/email/status` exposes configuration booleans without sender/recipient/credential values. `/health/live` returns 200 while the process is serving. `/health/ready` checks storage, completed migrations and the configured outbox threshold; unavailable storage or excessive backlog returns 503. AI/ElevenLabs/Zendesk are separately reported as degraded when simulated, unverified, failed or timed out, and do not make otherwise ready intake fail. Startup performs no live provider probes. [Signals](../../../docs/operations/OBSERVABILITY.md).

HTTP responses echo a validated `X-Correlation-ID` (or generate one) and W3C `traceparent`; ProblemDetails include `correlationId` and `traceId`. Logs are structured JSON and redact known secrets/detectable PII. `CIVICOPS_TELEMETRY=off|console|otlp` controls OpenTelemetry; demo forces remote OTLP off. Standard `OTEL_*` variables configure the collector and resource. No contact/narrative/provider payload, URL query or SQL text is deliberately exported as a span attribute. Redaction remains heuristic and telemetry access/retention require municipal review.


## Round 2: qualified dispatch, training, readiness and metrics

All incident intake now records a stable `workOrderTypeCode` from the shared versioned catalogue. Deterministic classification supplies it; model suggestions must match catalogue code/category/department. Critical hazards retain their qualification requirements. Full catalogue codes are Open311 service codes; legacy category aliases remain accepted. Unknown explicit codes return 400.

| Method / route | Purpose | Access |
|---|---|---|
| GET `/api/work-order-types` | Dispatcher catalogue for routing overrides | Dispatcher |
| GET `/api/incidents/{id}/crew-eligibility` | Department, availability, skill levels and unexpired certificate explanations | Dispatcher |
| POST `/api/incidents/{id}/work-order-type` | `{workOrderTypeCode,note,expectedVersion}`; audited override clears approval, assignment and stale recommendation; critical types floor Urgent | Dispatcher |
| GET `/api/integrations/readiness` | Configured/simulated/missing checklist with environment names, no values or provider probes | Dispatcher |
| GET `/api/metrics` | Audit-derived first-milestone medians, sample counts, AI/fallback intake counts, duplicate-candidate rate, digital-intake share | Dispatcher |
| POST `/api/retention/purge` | Run configured closed-case/contact retention now | Admin |
| GET `/openapi.json` | Registered routes, auth scopes and embedded portable training schemas | Public |
| GET `/api/v1/training/work-order-types`, `/skills`, `/certification-types` | Catalogue version + ETag/conditional GET | Training client |
| GET `/api/v1/training/workers/{id}/qualifications` | Synthetic qualifications and training records | Training client |
| GET `/api/v1/training/work-order-types/{code}/scenarios-hint` | Synthetic case-pattern hints; no incident data | Training client |
| POST `/api/v1/training/completions`, `/certifications` | Bounded, mandatory idempotency, audit-chained records and controlled simulation grants | Training client |
| GET `/api/v1/training/stream` | Named training/certification SSE signals, empty payload | Training client |
| GET `/api/training/audit`, `/api/training/audit/verify` | Training chain/state integrity | Dispatcher |

Certificate gaps follow `CIVICOPS_CERT_POLICY=warn|block`; demo defaults warn, other modes block. Skill, availability and department gaps always block. Assignment re-evaluates current qualifications. Catalogue SLA targets are operational targets, not promised arrival times.

`CIVICOPS_TRAINING_CLIENTS` is a separate semicolon-separated `name:sha256(token)` inventory. Training tokens cannot access incident or provider routes, including in demo. The public `civicops-demo-training` bearer exists only in explicit demo with no configured clients. See [the extension contract](../../../docs/EXTENSIONS.md) for payloads, schemas, rules and trusted-client supervisor attestation.

Retention is disabled unless `CIVICOPS_RETENTION_DAYS` is 1–36500. An hourly job removes only Closed snapshots after their closing milestone; legacy snapshots use their last update as a conservative fallback. Pending/dead-letter integration delivery protects a case. Hash-chained retired submission-key receipts prevent replay from recreating purged cases. Optional contacts are stored separately and purged with value-free audit tombstones. `CIVICOPS_PII_RETENTION_DAYS` independently clears closed-case contact fields while retaining the case. The purge also scrubs local integration payloads and related email outboxes. Historical audit and integration metadata remain: this does not erase all resident data or delete external tickets. File contents are flushed before same-filesystem replacement; directory fsync is not provided. PostgreSQL provides database transactions for durable state; file mode remains a single-process store. [Backend selection, migrations and import](../../../docs/operations/STORAGE.md) · [Backup/restore](../../../docs/operations/BACKUP_RESTORE.md).

Metrics retain audit history after snapshot purge. Legacy cases without intake measurement events are excluded from channel/duplicate/decision counts. Unfinished milestones have no sample; empty values are null. Web/Open311/chat share is a queue-avoidance proxy, with no queue-time or causal benefit measurement. Demo metrics are labelled synthetic. The dispatcher stream also emits `training.completed` and `certification.granted`; SSE is a bounded refresh signal, not a durable webhook delivery system.

## Enterprise staff and administration

`GET /auth/status` returns the current identity, roles, effective permissions and CSRF token. `/auth/login` challenges configured OIDC; POST `/auth/demo-login` is explicit-demo-only, POST `/auth/bootstrap-login` accepts administrator-issued local access, and POST `/auth/logout` ends the server ticket and configured federated session where supported. Local-login endpoints require CSRF before sign-in. Cookies remain Secure, HttpOnly and SameSite=Lax.

`/api/admin/overview`, `/tenants`, `/users`, `/service-accounts`, `/catalogue`, `/crews` and `/workers` require admin. Service create/rotate returns its token once; list/revoke never returns clear credentials. Catalogue PUT uses `{expectedVersion,catalogue}`, requires a higher version and rejects schema/reference violations. Tenant writes cannot change another municipality; tenant ids in operational bodies are ignored as selectors. APIs under `/m/{slug}` resolve public resident routing; authenticated principals must match that municipality. Generated `/openapi.json` includes policies, roles, cookie/bearer alternatives and CSRF/tenant metadata. [Full request contracts and scopes](../../../docs/RBAC.md).


## Administrative privacy requests

`GET /api/privacy/notice` publishes exact versioned notice/purpose and the configured `CIVICOPS_INFORMATION_OFFICER_CONTACT`, with an honest owner-pending label when blank. Admin-only, tenant-bound `/api/admin/privacy/requests` supports list and POST `{referenceNumber,action,requestNote}` for `access`, `correction` or `anonymisation`. Record verification separately with POST `/{id}/verify` and `{verificationNote}`; then POST `/{id}/fulfil`. The verification notes are a manual administrator assertion, not independently verified identity. Never submit identity-document scans.

Access returns a no-store JSON attachment containing the local case and separate contact record. Correction/anonymisation require `expectedVersion`. Correction accepts optional `description`, `contactEmail`, `contactPhone`, `consentToContact`; withdrawal removes contacts and reopens human review. Anonymisation clears current resident context, contacts, voice metadata, model evidence/drafts and local provider/email payloads and suppresses further queued delivery. Requests have chained audit evidence and digest-bound state. Legacy immutable audit, provider/backup/export copies and institutional legal holds remain Information Officer work. [Exact workflow and limits](../../../docs/privacy/POPIA.md).

All endpoints are inventoried in [input limits](../../../docs/security/INPUT_LIMITS.md); [HTTP security](../../../docs/security/HTTP_SECURITY.md) describes nonce CSP, conditional provider origins, HTTPS HSTS and cookie-CSRF exemptions. [Secret sources](../../../docs/security/SECRETS.md) covers the blank `*_FILE` credential inventory and fail-closed source handling.
