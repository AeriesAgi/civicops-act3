# 0007 — Recoverable Zendesk delivery through an outbox

- Status: accepted; live Zendesk delivery and account mapping remain unverified.
- Date recorded: 2026-10-08 (SAST).
- Scope: ticket creation, private lifecycle synchronization and approved public comments.

## Context and Problem Statement

A provider outage must not lose an accepted report or hold a municipal database transaction open. Conversely, retrying after an uncertain HTTP result can duplicate a ticket or comment. Approved drafts need to retain their original text even if the case changes before delivery.

## Decision Drivers

- Persist intent before delivery and recover after restart.
- Preserve per-case ordering without letting one failed case block other cases.
- Bound retries, provider calls and reconciliation.
- Expose simulation and dead letters rather than imply guaranteed exactly-once delivery.

## Considered Options

- Make synchronous Zendesk writes a prerequisite for accepting a report.
- Send best-effort background HTTP requests without durable state.
- Use durable intents, claims, bounded retries and downstream marker reconciliation.

## Decision Outcome

Choose [IntegrationOutbox.cs](../../products/civicops/Services/IntegrationOutbox.cs) and [ZendeskClient.cs](../../products/civicops/Services/ZendeskClient.cs). Deduplication keys identify creates, domain-status versions and each approved draft. Startup/periodic reconciliation recovers missing creates, the latest domain-status intent and every recorded approved comment; public comment intent stores the approved text. Historical communication delivery cannot revert the current downstream status.

PostgreSQL commits case/contact/audit/intents together through [DurableStorage.cs](../../products/civicops/Services/DurableStorage.cs). Delivery commits an attempt/lease before contacting Zendesk, releases the snapshot transaction during the network call and then commits the result. Hosted polling uses per-schema advisory leadership on a separate unpooled connection; persisted claims remain the correctness fence. File mode uses its exclusive process owner and recoverable reconciliation instead of claiming a cross-file transaction.

Transient failures use capped exponential backoff with bounded jitter. Permanent errors or exhausted attempts become visible dead letters and require an authorized retry. Per-case ordering protects later operations behind an unresolved delivery; other cases can proceed. The provider circuit and timeout bound outages.

Zendesk uses the case reference as `external_id` and an event marker in comments. Bounded external-id/comment search reconciles lost responses, rejects multiple matches and fails closed beyond its 20-page budget. [ZendeskEndpoints.cs](../../products/civicops/Services/ZendeskEndpoints.cs) verifies signed replies, suppresses CivicOps echoes and requests human review; replies cannot bypass lifecycle gates. Unconfigured/demo delivery saves exact simulated request artifacts and deterministic simulated ticket references.

## Consequences

Acceptance and ticket delivery are decoupled, with a reviewable recovery queue. A workflow request that omits `expectedVersion` can preserve concurrent delivery-only ticket/audit metadata through a locked comparison of its preparation snapshot. Explicit versions remain strict, and any concurrent human decision, safety/review, consent or geographic change causes a conflict; drafting remains outside the persistence lock. Delivery is **at least once**: external-id search is not a uniqueness constraint, indexing can lag and responses/leases can be lost. Operators still need duplicate reconciliation. Retry must not mean silently resetting a provider configuration error.

OAuth refresh, live account permissions/triggers, requester consent handling, separate municipal provider accounts and provider-side erasure require owner validation or additional engineering. Runtime credentials are global rather than per-tenant provider profiles. Simulation proves request shape and local workflow, not an actual Zendesk ticket.

## Confirmation

Implemented and tested locally: [OutboxTests.cs](../../tests/CivicOps.Tests/OutboxTests.cs) covers restart/reconciliation, draft preservation, backoff, ordering and dead letters; [ZendeskClientTests.cs](../../tests/CivicOps.Tests/ZendeskClientTests.cs) and [ZendeskWebhookTests.cs](../../tests/CivicOps.Tests/ZendeskWebhookTests.cs) exercise fake HTTP/signature behavior. [PostgresStorageTests.cs](../../tests/CivicOps.Tests/PostgresStorageTests.cs) and [ScalingBoundaryTests.cs](../../tests/CivicOps.Tests/ScalingBoundaryTests.cs) cover atomic intent and leadership. [smoke_e2e.sh](../../scripts/smoke_e2e.sh) verifies simulated delivery and audit. See [Zendesk setup](../ZENDESK_SETUP.md).
