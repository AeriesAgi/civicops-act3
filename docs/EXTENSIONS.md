# Crew and training extension contract

CivicOps exposes a versioned municipal work-order catalogue and a scoped training API. A future crew-training layer can use these contracts without coupling its scene code to incident internals. No VR, WebXR, 3D or headset code is implemented here. All worker identities and scenario hints in this build are synthetic.

The bundled baseline catalogue is [`products/civicops/catalog/work-orders.v1.json`](../products/civicops/catalog/work-orders.v1.json). The authoritative catalogue for a request is the signed-in municipality's active version, including any validated/audited admin revision. It contains stable work-order, skill and certification codes, department, hazard class, required skill levels, required certifications, PPE, typical procedure steps and SLA targets. Startup and admin editing validate references, codes, bounds and version. Procedure text and SLA targets are demonstration data and require municipal validation before operational use. [Catalogue administration and tenant binding](RBAC.md).

Catalogue changes increment its semantic `version`. Preserve code meanings across compatible versions; add a new code when meaning changes. Responses expose the version in their body and `X-CivicOps-Catalog-Version`, plus an `ETag` of the entire catalogue. Supply `If-None-Match` to receive `304` when it matches. The API retains `Cache-Control: no-store`; clients may retain their explicitly fetched catalogue version and perform conditional requests.

## Authentication and scope

For municipality-bound clients, an admin creates a service account with `training:read` for catalogue/qualification/SSE access and `training:write` for completion/certification writes. Supply both only when the integration needs both; no training scope grants incident access, dispatch approval or administration. The once-shown clear token is held in the training layer's trusted backend and sent as `Authorization: Bearer <token>` over HTTPS. Accounts bind their municipality on every request and enforce configured expiry, overlap rotation and revocation. See [service-account scope and lifecycle](RBAC.md).

Legacy environment bootstrap clients remain supported in the **default municipality only**. Set `CIVICOPS_TRAINING_CLIENTS` to semicolon-separated `name:sha256(token)` pairs. Names accept 1–80 ASCII letters, digits, `.`, `_` or `-` and must begin with a letter or digit. Hashes are exactly 64 hexadecimal characters. Raw tokens are never configured or persisted by CivicOps. Duplicate names, duplicate hashes and overlap with dispatcher token hashes fail startup. Keep clear tokens in a trusted backend, never in a published browser bundle.

Legacy training bearers can access only routes in `/api/v1/training/`; a recognized legacy bearer on another route returns `403`. Training-only service accounts cannot report/inspect/approve/assign incidents, access audit or invoke provider tools. An administrator may deliberately grant other service scopes, which are enforced separately; training scopes never imply those permissions. Interactive training-client sessions may use identity endpoints to sign out. Dispatcher credentials do not authenticate training routes. Missing/invalid credentials return `401`; an unconfigured live legacy training path returns `503`, while a valid scoped service account needs no environment bootstrap list. Authentication executes before rate limiting. `CIVICOPS_TRAINING_LIMIT` defaults to 60 requests per minute (valid range 1–10,000); partitions include municipality and authenticated principal, with process-local counters. There is no anonymous training quota to exhaust.

Only when `CIVICOPS_DEMO_MODE=true` and no training clients are configured, the simulated client `demo-training` accepts the public demonstration token `civicops-demo-training`. This token has no authority outside training routes and is never available in live mode. Configuring training clients disables that default demo client.

## API v1

All requests and responses use camel-case JSON. Write routes reject missing, duplicate and additional fields. Write bodies are bounded before JSON parsing, including chunked requests. Errors return `{ "error": "..." }` or standard ASP.NET problem JSON.

| Method | Path | Contract |
|---|---|---|
| GET | `/api/v1/training/work-order-types` | `{version, workOrderTypes}`; full hazards, PPE, steps, requirements and SLA; version/ETag headers |
| GET | `/api/v1/training/skills` | `{version, skills}`; shared skill codes and disciplines |
| GET | `/api/v1/training/certification-types` | `{version, certificationTypes}`; validity months and issuing basis |
| GET | `/api/v1/training/work-order-types/{code}/scenarios-hint` | Two generic synthetic case-pattern hints; no actual incident or resident information |
| GET | `/api/v1/training/workers/{id}/qualifications` | Synthetic worker's skill levels, certifications, recorded completions and `synthetic: true` |
| POST | `/api/v1/training/completions` | Record a completion; 16 KiB maximum; mandatory `Idempotency-Key` |
| POST | `/api/v1/training/certifications` | Grant/renew an eligible simulation certification; 8 KiB maximum; mandatory `Idempotency-Key` |
| GET | `/api/v1/training/stream` | Authenticated SSE with `training.completed` and `certification.granted` event names and empty `{}` payloads |

An idempotency key has 1–128 non-control characters. Keys are scoped to the municipality, authenticated client and write operation. An exact replay returns `200` with the original record and `replayed: true`; the first accepted request returns `201` and `replayed: false`. A key reused with a different parsed payload returns `409`. Idempotency survives application restart. Completion time must be supplied as an ISO timestamp at or before server time, with five minutes of clock tolerance. Scores range from 0 to 100, durations from 1 to 86,400 seconds, and attempts from 1 to 1,000. Skills must contain 1–32 unique catalogue codes. Unknown workers return `404`.

Completion example (all values are synthetic):

```json
{
  "workerId": "synthetic-water-01-a",
  "moduleId": "municipal-water-isolation-v1",
  "workOrderTypeCode": "WATER.BURST_MAIN",
  "skillsPractised": ["WATER.PIPE_REPAIR"],
  "score": 92,
  "passed": true,
  "durationSeconds": 480,
  "attempt": 1,
  "deviceClientLabel": "synthetic-training-client",
  "evidenceHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "completedAt": "2026-10-08T08:00:00Z"
}
```

Discover actual worker IDs from the synthetic crew roster and actual required skill codes from the catalogue; the example illustrates the envelope. Evidence is a SHA-256 digest only. Raw recordings, personal information and device identifiers must stay outside this API. `moduleId` and `deviceClientLabel` are caller labels, not verified module identities or hardware attestations. The service records declared practice; completion does not increase a worker's skill level automatically.

Certification example:

```json
{
  "workerId": "synthetic-water-01-a",
  "certificationTypeCode": "SAFETY.PPE",
  "completionId": "trn-<id from the completion response>",
  "supervisorSignOff": true,
  "signOffReference": "synthetic-supervisor-attestation-001"
}
```

The certification type must have issuing basis `simulation`, be required by the completion's work-order type, and match the same worker. Completion must declare `passed: true`, meet `CIVICOPS_TRAINING_PASS_SCORE` (default 80; range 0–100), and practise every required work-order skill. Supervisor sign-off is mandatory. Set `CIVICOPS_TRAINING_CERTIFICATIONS_ENABLED=false` to disable new grants. External, classroom and field-assessment certificates cannot be granted through this endpoint. Validity starts at server issue time and uses the catalogue's validity months; expiry and evidence reference are server generated.

`supervisorSignOff` and `signOffReference` are attestations submitted by an authenticated, trusted training client. This build does not verify a separate supervisor identity, signature, accredited curriculum or regulatory entitlement. Configure that verification in the training client's trusted backend before allowing it to attest live grants. An external certificate always needs a separate verified municipal workflow. A successful simulation never grants an external certificate.

## Durability, audit and assignment

Both storage backends persist municipality-bound completions, certification grant intents, idempotency fingerprints and a SHA-256 chain authenticating immutable training records, plus workforce/crew state and its audited grant projection. Corrupt chains or snapshots fail closed. In file mode these are `training.json` and `workforce.json` in the tenant partition; flushed atomic writes precede acknowledgement, and startup/exact replay repairs interrupted grant/global-audit projections. One process owns the private file data directory. In PostgreSQL mode the same snapshots are authoritative durable documents in the database, with training/workforce/shared-audit mutations joining the transaction and fresh state protected by the database lock; no municipal JSON mirror is written locally. [Storage, import and tested contracts](operations/STORAGE.md), [backup/restore](operations/BACKUP_RESTORE.md).

Each accepted write also appears as a system event in that municipality's incident audit chain. `/api/training/audit` and `/api/training/audit/verify` require the audit policy: supervisor, auditor, admin or an appropriately scoped `audit:read` service account; ordinary dispatchers and training-only clients cannot access them. Verification exposes only the current municipality's chain. Local hash chains detect tampering but have no external timestamp/anchor in this build. [Permission matrix](RBAC.md), [signed audit exports and custody limits](operations/AUDIT_EXPORT.md).

Assignment eligibility uses the same catalogue requirements and current workforce qualifications. Missing skills, department mismatch and unavailable crews always block assignment. `CIVICOPS_CERT_POLICY=warn|block` selects whether expired or missing certificates allow an assignment with a warning or block it. The default policy is `warn` in demo and `block` outside demo. Simulation credentials satisfy only matching simulation requirements; they cannot replace external requirements. Dispatcher work-order overrides are version checked and audited. The dashboard exposes eligibility reasons and current certification expiry.

## Events and layer boundaries

The operations `/api/stream` also emits `training.completed` and `certification.granted`. Subscriptions capture their municipality, and the training stream additionally filters out incident refresh events. Both streams send event names with `{}` only; consumers fetch permitted data through authenticated routes. SSE is a process-local refresh signal with a 16-event queue that drops oldest events, not a durable delivery log: reconnect, refresh qualifications, and retry writes with the original idempotency key. There is no training outbound webhook, cross-node fan-out or durable replay. PostgreSQL's shared durable state does not distribute sessions/quotas/SSE; the supported deployment remains one active HTTP instance. [Scaling boundaries](operations/DEPLOYMENT.md).

The future layer belongs under [`layers/training/`](../layers/training/README.md). Its trusted backend authenticates to this API, maps catalogue codes to scenarios, validates supervisor attestations, hashes evidence and submits records. Its client can consume short-lived credentials issued by that backend; do not embed CivicOps bearer tokens in downloadable assets. Keep headset rendering, session telemetry, curriculum and evidence storage in the layer; CivicOps remains the municipal incident, assignment and qualification authority.

JSON Schema Draft 2020-12 artifacts are in [`docs/schemas/`](schemas/):

- `work-order-catalog.schema.json`, `work-order-types-response.schema.json`, `skills-response.schema.json`, `certification-types-response.schema.json`
- `training-completion.schema.json`, `training-completion-response.schema.json`
- `training-certification.schema.json`, `training-certification-response.schema.json`
- `worker-certification.schema.json`, `worker-qualifications.schema.json`

Schemas describe structural contracts; catalogue membership, expiry, score policy, matching completion, clock tolerance and authentication are additionally enforced by the server.
