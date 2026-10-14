# 0011 — A versioned, scoped training-layer extension API

- Status: accepted; curriculum, independent supervisor verification and a rendering layer remain owner-pending.
- Date recorded: 2026-10-08 (SAST).
- Scope: catalogue discovery, qualifications, completions and controlled simulation certification.

## Context and Problem Statement

A future training product must share work-order, skill and certification meanings with municipal assignment without depending on incident internals or exporting resident material into training scenarios. Completion records need durable replay protection, but a passing simulation must not invent a field/external certification or regulatory entitlement.

## Decision Drivers

- Keep one validated catalogue for intake, eligibility and training.
- Separate training credentials/routes from case and dispatch authority.
- Preserve idempotent records and recover interrupted certification projections.
- Keep rendering, curriculum, device telemetry and raw evidence outside CivicOps.

## Considered Options

- Embed VR/headset/curriculum code inside the incident application.
- Let a training client mutate incident and workforce records directly.
- Publish versioned scoped contracts through a dedicated extension boundary.

## Decision Outcome

Choose `/api/v1/training` in [TrainingEndpoints.cs](../../products/civicops/Services/TrainingEndpoints.cs). [WorkOrderCatalog.cs](../../products/civicops/Services/WorkOrderCatalog.cs) and the [catalogue](../../products/civicops/catalog/work-orders.v1.json) supply stable codes, references, hazard/qualification requirements, PPE, procedures and version. Responses expose body/header version and whole-catalogue ETag. Portable [JSON schemas](../schemas/) describe request/response structures; application validation additionally checks membership, timing and eligibility.

[TrainingAccess.cs](../../products/civicops/Services/TrainingAccess.cs) isolates hashed configured clients and per-client quotas; scoped service accounts can separately grant training read/write. Training identities cannot acquire incident, provider-tool, approval or assignment authority. [TrainingStore.cs](../../products/civicops/Services/TrainingStore.cs) requires bounded strict payloads and an idempotency key scoped to client and operation. Exact replays recover the original result; changed content conflicts. Completions retain an evidence digest rather than raw recordings or device identity.

Certification grants require a matching worker/completion, passing score, required practised skills, applicable simulation certification type and a trusted-client supervisor sign-off assertion. External/classroom/field-assessment certificates cannot be granted through this endpoint. Completion alone does not raise skills or issue a certificate. Save the immutable grant intent before projecting it to [WorkforceStore.cs](../../products/civicops/Services/WorkforceStore.cs); restart/replay recovery repairs interrupted projection and missing shared audit without duplication. Both storage backends retain authenticated histories.

Training and dispatcher SSE emit named `training.completed` / `certification.granted` signals with empty payloads. SSE is a refresh hint, not a durable replay log or outbound webhook. Clients refresh authorized state after reconnect and retry writes with the same key.

## Consequences

A future layer can map stable catalogue codes to scenarios while CivicOps remains the qualification/assignment authority. Its trusted backend must hold tokens, validate supervisor attestations, protect evidence and issue appropriate client credentials. Tokens must not be embedded in downloadable browser/headset assets.

`supervisorSignOff` is an authenticated client's assertion, not independently verified supervisor identity, signature or accredited curriculum. Current workers/scenario hints remain synthetic. Municipal catalogue/procedure/qualification review is required before operational use. No VR, WebXR, 3D, headset or completed training product is implemented; the future boundary is [layers/training](../../layers/training/README.md).

## Confirmation

Implemented and tested locally: [TrainingTests.cs](../../tests/CivicOps.Tests/TrainingTests.cs) covers schemas, scope separation, payload bounds, replay, certification restrictions, recovery, tampering and SSE. [WorkforceExtensionTests.cs](../../tests/CivicOps.Tests/WorkforceExtensionTests.cs) proves shared qualification/assignment policy, and [TenantIsolationTests.cs](../../tests/CivicOps.Tests/TenantIsolationTests.cs) isolates training records. [Extension contract](../EXTENSIONS.md) is the detailed API guide. External certification verification and the training layer remain documented/owner-pending.
