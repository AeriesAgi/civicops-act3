# Zendesk: tickets, durable delivery, and verified replies

CivicOps can use an existing Zendesk Support account. No live Zendesk requests have been made in the local verification rounds. With incomplete credentials, or whenever `CIVICOPS_DEMO_MODE=true`, the integration is **simulated**: it persists the same API request JSON as a tenant-bound artifact and assigns a deterministic synthetic ticket id. File mode writes `zendesk-outbox/` under the municipality's data partition (`CIVICOPS_DATA_DIR` for `default`, or its `tenants/{id}` directory); PostgreSQL mode stores the artifacts as durable database documents. No account is needed for the local walkthrough or CI.

## Configure an existing account

1. Set `ZENDESK_SUBDOMAIN` to the label before `.zendesk.com` (for example the synthetic label `synthetic`), without a URL or path.
2. For an existing eligible API token, use **Admin Center → Apps and integrations → APIs → API tokens**. Set `ZENDESK_EMAIL` to the authorized agent's email and `ZENDESK_API_TOKEN` to the token through the private deployment's environment manager. The client sends Basic authentication for `{email}/token:{token}`. Never put these values in a checked-in environment file. [Zendesk authentication](https://developer.zendesk.com/api-reference/introduction/security-and-auth/), [API token management](https://support.zendesk.com/hc/en-us/articles/4408889192858-Managing-API-token-access-to-the-Zendesk-API)
3. For OAuth, create an OAuth client in **Admin Center → Apps and integrations → APIs → OAuth clients**, obtain an authorized access token through Zendesk's documented OAuth flow, and set `ZENDESK_OAUTH_TOKEN`. This takes precedence over Basic authentication. The app consumes the token; automatic OAuth acquisition/refresh is a deployment responsibility. [Zendesk OAuth guide](https://support.zendesk.com/hc/en-us/articles/4408845965210-Using-OAuth-authentication-with-your-application)
4. Set `CIVICOPS_DEMO_MODE=false` only for the approved private live runtime. Live ticket delivery begins once credentials are complete. The dashboard reports `live`, `simulated`, pending deliveries, and dead letters without exposing credentials.
5. Check the configured account explicitly with `dotnet run --project products/civicops -- zendesk --check`. It makes one read-only `GET /api/v2/users/me.json`, validates a positive current-user id, and prints only success/failure without the user's identity, token or provider payload. Normal startup never runs this check. Missing credentials or demo mode returns exit code `2` without HTTP; a successful check returns `0`, and a failed check returns `1`. Connectivity alone does not prove ticket-write permissions or webhook configuration. [Current-user API](https://developer.zendesk.com/api-reference/ticketing/users/users/#show-the-current-user)

As verified on 8 October 2026, Zendesk prevents accounts created on or after 28 July 2026 from using API tokens, stops new token creation for all accounts on 27 October 2026, and ends remaining API tokens on 30 April 2027. OAuth is therefore available in this implementation for newer accounts and migration. [Zendesk's token retirement announcement](https://support.zendesk.com/hc/en-us/articles/10840968198042-Announcing-the-removal-of-API-tokens-as-an-authentication-method-for-API-requests)

| Environment variable | Purpose |
| --- | --- |
| `ZENDESK_SUBDOMAIN` | Existing account label |
| `ZENDESK_EMAIL` | Authorized agent identity for Basic token authentication |
| `ZENDESK_API_TOKEN` | Existing eligible API token; secret |
| `ZENDESK_OAUTH_TOKEN` | OAuth access token; secret; overrides Basic |
| `ZENDESK_WEBHOOK_SECRET` | Zendesk webhook signing secret; separate from API auth |
| `CIVICOPS_DATA_DIR` | Private writable persistent directory; blank means app `Data` |
| `CIVICOPS_DEMO_MODE` | Explicit `true` forces simulated delivery |

Credential values also support private mounted `_FILE` sources; see [secret sources and rotation](security/SECRETS.md). Zendesk account credentials/signing secret are process-wide, while cases, outbox items and artifacts are municipality-bound. Distinct municipal Zendesk accounts require approved separate deployments or a new tested configuration layer; tenant partitioning does not select provider accounts. [Municipality onboarding](ONBOARDING_MUNICIPALITY.md).

## Create the return webhook

In **Admin Center → Apps and integrations → Webhooks → Webhooks**, create a webhook connected to a trigger or automation, named `CivicOps case changes`. Use HTTP `POST`, JSON request format, and the HTTPS endpoint `https://YOUR-APP-HOST/api/channels/zendesk/webhook`. Retrieve its signing secret from the webhook detail screen, and set `ZENDESK_WEBHOOK_SECRET` privately. CivicOps requires this secret even in demo mode when receiving Zendesk webhooks. [Managing webhooks](https://support.zendesk.com/hc/en-us/articles/4408836101146-Managing-webhooks), [creating webhooks](https://developer.zendesk.com/documentation/webhooks/creating-and-monitoring-webhooks/), [signature verification](https://developer.zendesk.com/documentation/webhooks/verifying/)

Create a ticket trigger for tickets tagged `civicops` when a comment or status changes. Its **Notify active webhook** action must emit this application-specific schema. This is an exact synthetic example of the accepted JSON, rather than an unverified Liquid filter recipe:

```json
{
  "eventId": "fake-ticket-update-001",
  "caseReference": "CIV-20261008-FAKE0001",
  "ticketId": 123,
  "status": "open",
  "comment": "Synthetic resident reply for dispatcher review.",
  "source": "zendesk"
}
```

Map `caseReference` from `ticket.external_id`, `ticketId` from `ticket.id`, `status` from `ticket.status`, and `comment` from the latest comment collection's value. Zendesk documents `comment.id` through its comment collections, including `ticket.latest_comment`; access it through a Liquid iteration rather than assuming `ticket.latest_comment.id` is a scalar property. Combine ticket id, comment id, `ticket.updated_at_with_timestamp`, and status for a stable event id, or supply the native originating event id through an existing relay. Avoid `ticket.updated_at`, which is a display date rather than an ISO timestamp. [Zendesk placeholder reference](https://support.zendesk.com/hc/en-us/articles/4408886858138-Placeholder-reference-for-business-rules)

`eventId` must remain stable on retries. Render string values with proper JSON escaping in the account's trigger editor or an existing relay; verify a synthetic reply containing quotes, a backslash, and a newline. Do not assume an undocumented Liquid `json` filter exists or paste arbitrary comment text into JSON without escaping. The receiver's schema is independent of native event subscription envelopes. The owner must verify the chosen account's trigger rendering before the private pilot; no trigger was created during this round.

The receiver verifies the documented `X-Zendesk-Webhook-Signature` and `X-Zendesk-Webhook-Signature-Timestamp` headers using base64 HMAC-SHA256 of `timestamp + raw UTF-8 body`, constant-time comparison, and a five-minute freshness window. It bounds the body to 64 KiB and validates all fields. Keep system clocks synchronized. Invalid signatures return 401, missing verification configuration returns 503, unknown case references return 404. Replayed event ids return 200 without adding another audit entry.

Bind the webhook to the approved municipality through its configured resident host/path or a tenant-bound service account with `zendesk:webhook` scope, according to [RBAC.md](RBAC.md). The HMAC signature remains required even for a valid service bearer, and a payload municipality field cannot choose the tenant. Verify actual account/relay routing, signature preservation and a wrong-tenant reference denial before enabling production replies.

Webhook replies and upstream status changes are added to the hash-chained case audit and flagged for a dispatcher. They do not approve, assign a crew, or move the CivicOps dispatch lifecycle. Comments containing `[CivicOps event ...]`, and messages explicitly marked `source: civicops`, are ignored to prevent integration loops.

## Ticket and outbox behavior

Every creation sets `external_id` to the CivicOps reference, with category/channel tags, mapped priority, and a **private** note containing the triage rationale and recorded agent provenance. Requester information appears only when valid contact information and explicit consent are both present. Status synchronization adds private operational notes. Only the explicit `approved-comment` operation publishes a resident update.

Priority mapping is Urgent → urgent, High → high, Medium → normal, Low → low. Active CivicOps states map to open, WaitingForCitizen maps to pending, and Resolved/Closed map to solved; Zendesk closes solved tickets through its own automation. [Tickets API](https://developer.zendesk.com/api-reference/ticketing/tickets/tickets/)

The durable queue checkpoints attempts before a delivery and reconciles saved cases missing their create delivery, latest domain status, or approved communications on startup and every five seconds. Stable audit event indices identify status intents; approved draft ids identify public comment intents. Recovery uses each draft's recorded text and requires its approved state, dispatcher identity, and approval timestamp. Recovered historical comments omit ticket status fields so they cannot revert a newer status.

The worker preserves operation order within each case, uses capped exponential backoff from a default five-second base with 0–20% positive jitter, and dead-letters after the default five failed attempts. Most HTTP 4xx errors dead-letter immediately; 408, 409, and 429 remain retryable. Other cases continue while one case backs off. Dispatcher-visible errors contain HTTP status or a generic failure label, without provider response bodies or credentials. Bounded request timeouts and provider circuit breakers prevent an unavailable account from blocking intake indefinitely.

Before creating a ticket, the client searches for the exact `external_id`, follows response pages, and refuses ambiguous multiple ticket ids. After an uncertain update response it follows ticket-comment pages for the delivery marker before appending another comment. Both reconciliations have a 20-page budget and fail closed when it is exhausted; pagination URLs must remain on the configured account's HTTPS `/api/v2/` origin. Responses are limited to 1 MiB and bounded transport/read timeouts. [Search API](https://developer.zendesk.com/api-reference/ticketing/ticket-management/search/), [ticket comments](https://developer.zendesk.com/api-reference/ticketing/tickets/ticket_comments/)

Inspect `GET /api/integrations/outbox` with an authorized operations-read staff session. It returns current-municipality delivery metadata, excluding raw request JSON. After correcting a permanent configuration error, a supervisor/admin or service account with `integrations:write` can use `POST /api/integrations/outbox/{id}/retry` to requeue a dead letter while preserving attempt history and retry deduplication. An ordinary dispatcher cannot retry. `GET /api/channels/zendesk/status` returns only the safe mode and aggregate counts; follow the [permission matrix](RBAC.md).

File storage/outbox require one CivicOps process and a persistent private volume. PostgreSQL storage/outbox are implemented and locally tested: case/contact/audit/delivery-intent writes commit transactionally; fresh reads and version/claim leases fence concurrent attempts; hosted workers use schema-scoped advisory leadership on a separate connection. Provider delivery happens outside the snapshot transaction, so it remains **at least once**, not an atomic database/provider write. SSE, staff sessions and quotas still require one active HTTP application instance. [Storage contracts](operations/STORAGE.md), [deployment limits](operations/DEPLOYMENT.md).

Reconciliation restores the current domain status rather than historical intermediate statuses absent from a legacy intent log; PostgreSQL now commits new domain intents with the case mutation. Zendesk search is eventually consistent and `external_id` is not a uniqueness constraint: an uncertain creation before indexing can still duplicate a live ticket. OAuth token refresh, reconciliation beyond the bounded page budget, actual municipal provider permissions/trigger behavior and production capacity remain owner work. Local idempotency, both-backend persistence/recovery, queue leadership/claims, deterministic simulation, backoff/dead letters, paginated reconciliation, request shaping and HMAC/tenancy behavior are tested. No live Zendesk delivery is claimed.

During an outage preserve live configuration and durable queue evidence, correct the cause, reconcile tickets/markers and retry authorized failures. **Do not enable demo or remove credentials on a live pending queue**: the simulated client can complete entries with synthetic IDs. No independent queue-pause switch is implemented; urgent compromised-credential containment may require stopping the application before owner-managed rotation. See [provider outage playbook](operations/RUNBOOK.md).
