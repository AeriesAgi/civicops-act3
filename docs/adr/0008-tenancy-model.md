# 0008 — Trusted municipality binding and tenant partitions

- Status: accepted; production municipal routing and separate provider deployment choices remain owner-pending.
- Date recorded: 2026-10-08 (SAST).
- Scope: request, store, credential, rate-limit and stream isolation.

## Context and Problem Statement

CivicOps can serve more than one municipality, but a tenant selector in request JSON must never allow access to another municipality's cases, crews, credentials or audit. Legacy single-municipality data must remain readable without making its unbound records reusable under a new tenant. Shared routing metadata needs a deliberate, limited exception.

## Decision Drivers

- Resolve tenant authority from authenticated identity or configured resident routing.
- Keep durable state and replay markers isolated on both storage backends.
- Capture tenancy in background work and stream subscriptions.
- Preserve historical default-tenant data without weakening new partitions.

## Considered Options

- Trust a tenant ID supplied with each request body.
- Require a separate application/database deployment for every tenant.
- Use trusted request binding and application-enforced partitions, with separate deployments still available.

## Decision Outcome

Choose [TenantContext.cs](../../products/civicops/Services/TenantContext.cs) and [TenantStore.cs](../../products/civicops/Services/TenantStore.cs). Authenticated requests use `civicops:tenant`; public resident requests use a configured hostname or `/m/{slug}/` route. [EnterpriseAccessMiddleware.cs](../../products/civicops/Services/EnterpriseAccessMiddleware.cs) rejects mismatched authenticated host/path routing and unknown tenant routing. A body tenant never changes authority. Tenant IDs are bounded lowercase letters/digits/hyphens; request-local leases restore the prior context across asynchronous work.

Each operational store binds records and logical paths to the current municipality. File data uses a tenant directory; PostgreSQL stores tenant-bound documents/projections through [DurableStorage.cs](../../products/civicops/Services/DurableStorage.cs). Loaded snapshots reject records belonging to another municipality. Legacy unbound records migrate only to `default`, preserving old hashes and adding migration evidence; copying them into another partition fails closed.

Incidents, optional contacts, privacy requests, workforce, training, catalogue edits, identity records, audit, delivery intents and voice receipts are isolated. Background delivery/recovery/retention iterates explicit tenant leases. [CaseEventStream.cs](../../products/civicops/Services/CaseEventStream.cs) captures the subscriber tenant and publishes refresh signals with empty payloads. [RequestHardening.cs](../../products/civicops/Services/RequestHardening.cs) includes municipality in quota partitions. Default-tenant environment credentials remain bootstrap clients, while new service accounts bind their own tenant.

The municipality directory intentionally shares limited routing/administrative metadata; it contains no resident case, workforce or credential payload. A default-tenant admin can provision tenant metadata without gaining another municipality's operational authority.

## Consequences

Tenants can share an application and durable backend without body-selected cross-tenant access. This is application enforcement, **not PostgreSQL row-level security or separate encryption keys/databases per municipality**. Database/host administrators remain privileged, and one application's global write lock is a shared performance boundary.

OIDC, ElevenLabs, Zendesk and other provider options are one runtime configuration. Tenant data isolation does not automatically create independent issuers/accounts or per-tenant secret profiles. Owners requiring those boundaries must choose separate single-active deployments or implement/review provider profiles. Shared sessions/quotas/SSE across nodes also require engineering; the current release remains one active application instance.

## Confirmation

Implemented and tested locally: [TenantIsolationTests.cs](../../tests/CivicOps.Tests/TenantIsolationTests.cs) verifies context isolation, data/replay/audit/workforce/training partitions and legacy rebinding rejection. [TenantEndpointTests.cs](../../tests/CivicOps.Tests/TenantEndpointTests.cs) verifies routing and cross-tenant endpoints; [ServiceAccountTests.cs](../../tests/CivicOps.Tests/ServiceAccountTests.cs) protects credential tenant binding. Shared contracts run on file/PostgreSQL. See [RBAC and tenancy](../RBAC.md) and [storage limits](../operations/STORAGE.md). Real municipal host/DNS/ingress and account separation remain owner validation.
