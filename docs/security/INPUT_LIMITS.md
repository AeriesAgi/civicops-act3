# Request validation and endpoint review

Implemented and tested in round 5, with the round-6 health and signed-export routes added below. This is a review of every mapped method/route in `PermissionMatrix`; static UI assets are subject to CSP and Kestrel limits. New endpoints fail startup if no explicit permission policy exists.

`RequestHardening` enforces 32-KiB default bodies, including chunked requests and DELETE, with explicit catalogue/webhook/training bounds below. JSON maximum depth is 32, maximum array length 1,000, maximum total nodes 12,000, string ceiling 65,536 UTF-16 units, and property names 128. Duplicate keys (case-insensitive), invalid surrogate strings, bidi/invisible format controls, NUL/control abuse and more than eight consecutive combining marks are rejected. Legitimate accents and supplementary Unicode remain supported. JSON bytes are never rewritten before webhook HMAC verification. State-changing bodies require JSON; empty state-changing bodies remain supported where the endpoint accepts them. OIDC callback response types remain under the framework state/nonce handler.

Metadata limits: path 2,048 characters, query 4,096, at most 64 headers, generic header values 8,192, bearer 1,031, idempotency key 200, conversation header 128, tool credential 4,096. Protected headers accept one value, reject control characters, and never become response header content. Kestrel also supplies its native HTTP limits; malformed headers rejected before application middleware may use native server error formatting.

Narratives/notes have 4,000/2,000 bounds; areas 200; email 254; phone 40 with an ASCII telephone-character and 7–15-digit allowlist. Area whitespace and narrative Unicode are canonicalised to NFC. Language/contact choice, service/work-order code, tools, status transitions, roles, tenant/crew IDs, scopes, certifications and catalogue references are allowlisted. Consent must explicitly match the current version in production intake. Training accepts every schema field exactly once and rejects unknown fields. Administrative catalogue updates require valid bounded reference graphs and increasing versions. Provider payloads use documented bounded scalar envelopes; additional unused provider fields do not grant actions.

Validation and quota failures use ProblemDetails; legacy `error`/`message` aliases and a fresh `incident` conflict snapshot remain compatible. Unhandled failures return a generic message and trace ID, with no stack trace or request text. Kestrel's product server header is disabled.

Quotas are fixed minute windows with no queue. Intake uses `CIVICOPS_INTAKE_LIMIT` (60), dispatch `CIVICOPS_DISPATCH_LIMIT` (120), auth `CIVICOPS_AUTH_LIMIT` (20), tools a shared `CIVICOPS_TOOL_LIMIT` (120) plus the existing per-conversation intake budget, and training `CIVICOPS_TRAINING_LIMIT` (60). Trusted municipality and authenticated principal partition budgets; anonymous/demo compatibility requests use IP. Rotating conversation IDs cannot evade the shared tool cap. Invalid training credentials use a separate tenant/IP auth budget and cannot consume an authenticated trainer's quota. Legacy dispatch unauthorized attempts keep a separate IP budget. Budgets reset on restart; multi-node enforcement, trusted ingress/IP forwarding and volumetric defence remain owner-pending. Public health/schema/configuration-label endpoints intentionally have no application quota; ingress remains responsible for them.

Evidence: `RequestHardeningTests`, `FoundationTests`, `TrainingTests`, `AdminConsoleTests`, `TenantEndpointTests`, `CsrfTests`, `SecurityHeadersTests`, `PrivacyTests`, HTTP/browser smokes and the permission matrix tests. Error-body compatibility and webhook raw-byte signatures remain tested.

## All mapped endpoints

| Method | Route | Permission | Maximum body |
|---|---|---|---|
| GET | /healthz | public | none |
| GET | /health/live | public | none |
| GET | /health/ready | public | none |
| GET | /openapi.json | public | none |
| GET | /api/email/status | public | none |
| GET | /api/ai/triage-contract | public | none |
| GET | /api/ai/status | public | none |
| GET | /api/spend-caps | public | none |
| GET | /api/channels/status | public | none |
| GET | /api/channels/elevenlabs/status | public | none |
| GET | /api/channels/zendesk/status | public | none |
| GET | /open311/v2/services | public | none |
| GET | /auth/status | identity-public | none |
| GET | /auth/login | identity-public | none |
| POST | /auth/demo-login | identity-public | 32 KiB |
| POST | /auth/bootstrap-login | identity-public | 32 KiB |
| POST | /auth/logout | authenticated | 32 KiB |
| GET | /api/incidents/{id} | resident-read | none |
| GET | /api/channels/elevenlabs/session | resident-read | none |
| POST | /api/incidents | resident-write | 32 KiB |
| POST | /open311/v2/requests | resident-write | 32 KiB |
| POST | /api/channels/elevenlabs/simulated-chat | resident-write | 32 KiB |
| GET | /api/dispatcher | staff-identity | none |
| GET | /api/crews | workforce-read | none |
| GET | /api/work-order-types | workforce-read | none |
| GET | /api/incidents | incident-read | none |
| GET | /api/dispatch/incidents/{id} | incident-read | none |
| GET | /api/incidents/{id}/crew-eligibility | operations-read | none |
| GET | /api/metrics | operations-read | none |
| GET | /api/integrations/readiness | operations-read | none |
| GET | /api/integrations/outbox | operations-read | none |
| GET | /api/channels/elevenlabs/alerts | operations-read | none |
| GET | /api/stream | operations-read | none |
| POST | /api/incidents/{id}/approve | dispatch | 32 KiB |
| POST | /api/incidents/{id}/assign | dispatch | 32 KiB |
| POST | /api/incidents/{id}/comms/{draftId}/approve | dispatch | 32 KiB |
| POST | /api/incidents/{id}/callback | dispatch | 32 KiB |
| POST | /api/demo/seed | dispatch | 32 KiB |
| PATCH | /api/incidents/{id}/status | work-status | 32 KiB |
| POST | /api/incidents/{id}/work-order-type | supervise | 32 KiB |
| GET | /api/evidence/export | audit | none |
| GET | /api/audit | audit | none |
| GET | /api/audit/verify | audit | none |
| GET | /api/audit/export | audit | none |
| POST | /api/audit/export/local | audit | 32 KiB; accepts no payload |
| GET | /api/training/audit | audit | none |
| GET | /api/training/audit/verify | audit | none |
| POST | /api/retention/purge | admin | 32 KiB |
| POST | /api/admin/tenants | admin | 32 KiB |
| POST | /api/admin/users | admin | 32 KiB |
| POST | /api/admin/service-accounts | admin | 32 KiB |
| POST | /api/admin/service-accounts/{id}/rotate | admin | 32 KiB |
| POST | /api/admin/service-accounts/{id}/revoke | admin | 32 KiB |
| POST | /api/admin/crews | admin | 32 KiB |
| POST | /api/admin/workers | admin | 32 KiB |
| GET | /api/privacy/notice | public | none |
| POST | /api/admin/privacy/requests | admin | 32 KiB |
| POST | /api/admin/privacy/requests/{id}/verify | admin | 32 KiB |
| POST | /api/admin/privacy/requests/{id}/fulfil | admin | 32 KiB |
| GET | /api/admin/privacy/requests | admin | none |
| GET | /api/admin/overview | admin | none |
| GET | /api/admin/tenants | admin | none |
| GET | /api/admin/users | admin | none |
| GET | /api/admin/service-accounts | admin | none |
| GET | /api/admin/catalogue | admin | none |
| GET | /api/admin/crews | admin | none |
| GET | /api/admin/workers | admin | none |
| PUT | /api/admin/tenants/{id} | admin | 32 KiB |
| PUT | /api/admin/users/{id} | admin | 32 KiB |
| PUT | /api/admin/catalogue | admin | 512 KiB |
| PUT | /api/admin/crews/{id} | admin | 32 KiB |
| PUT | /api/admin/workers/{id} | admin | 32 KiB |
| POST | /api/admin/users/{id}/revoke | admin | 32 KiB |
| POST | /api/admin/users/{id}/rotate | admin | 32 KiB |
| POST | /api/channels/elevenlabs/tools/{tool} | tools | 32 KiB |
| POST | /api/channels/zendesk/webhook | zendesk-webhook | 64 KiB |
| POST | /api/channels/elevenlabs/post-call | elevenlabs-webhook | 512 KiB |
| GET | /api/v1/training/work-order-types | training-read | none |
| GET | /api/v1/training/skills | training-read | none |
| GET | /api/v1/training/certification-types | training-read | none |
| GET | /api/v1/training/work-order-types/{code}/scenarios-hint | training-read | none |
| GET | /api/v1/training/workers/{id}/qualifications | training-read | none |
| GET | /api/v1/training/stream | training-read | none |
| POST | /api/v1/training/completions | training-write | 16 KiB |
| POST | /api/v1/training/certifications | training-write | 8 KiB |
| POST | /api/integrations/outbox/{id}/retry | integration-retry | 32 KiB |

Review inventory: **85 method/route pairs**. Local audit export takes its target from protected configuration and requires cookie CSRF; callers cannot supply a filesystem path. Public global health probes bypass database-dependent tenancy/credential lookups, while readiness owns bounded storage checks. Signed-export role/CSRF/tamper tests and global-probe tests cover these additions. For field-level contracts see [API](../../products/civicops/docs/api.md), [training schemas](../schemas/), [privacy](../privacy/POPIA.md) and [RBAC](../RBAC.md).
