# CivicOps proposed service objectives

These are proposed production objectives, not achieved availability claims. The application emits the required signals and has synthetic failure/timeout tests. The owner must approve the objectives, deploy collectors/synthetic probes and alert receivers, and measure real municipal workloads before a resident pilot. Dashboards and alert rules below are documented-only; no live notification service was called.

Use a rolling 30-day window, excluding an explicitly separated synthetic/demo environment. Planned maintenance is counted unless the municipality publishes an agreed exception in advance. Do not exclude provider outages from intake availability: deterministic fallback and durable queueing must keep reporting available.

| SLI | Proposed SLO | Window and denominator |
|---|---|---|
| Intake availability | **99.9%** | Successful HTTP intake response with durable case/audit/queue evidence divided by valid intake attempts; include 5xx, storage failures and synthetic probe timeouts; exclude malformed/unauthorised 4xx and count idempotency replay as an available response |
| Triage latency | **p95 ≤ 30 seconds** | Extraction-through-triage custom histogram for accepted reports; report slow/error intake separately to avoid survivor bias |
| Outbox delivery latency | **95% ≤ 5 minutes**, **99% ≤ 15 minutes** | Creation-to-delivery for live Zendesk intents; pending age and dead letters remain in the unmet population, so completed-only histograms cannot establish the objective alone |
| Audit integrity | **100% verified chains**, **zero unexplained gaps** | All municipality chains plus verified signed exports and restore drills; missing/stale verification is unknown, never success |

Intake's 0.1% monthly error budget allows about 43.2 minutes of equivalent downtime in 30 days (43,200 minutes), or 0.1% of eligible requests. Triage's tail budget is 5% above 30 seconds. Delivery's budgets are 5% above five minutes and 1% above fifteen. Audit has zero corruption budget; any unexpected verification failure requires immediate investigation and suspension of affected writes. These objectives measure processing/delivery; municipal review/crew arrival targets are separate catalogue policies, not promised response times.

## Alert rules to configure

| Alert | Threshold / duration | Action |
|---|---|---|
| Intake rapid budget burn | Error ratio ≥ 1.44% (14.4× budget) in both 5-minute and 1-hour windows; at least 20 valid attempts or a failed independent probe | Page duty operator; inspect storage and trace; retain intake/reference evidence |
| Intake sustained burn | Error ratio ≥ 0.6% (6× budget) in both 30-minute and 6-hour windows | Page; assess budget and pause risky deployments |
| Triage latency | p95 > 30 seconds for 10 minutes with at least 20 reports | Check inference/network timeout and deterministic fallback performance |
| AI fallback | > 20% for 10 minutes in a configured live environment, at least 20 decisions | Warn; examine provider circuit, schema rejection and model health; demo fallback is expected |
| Outbox age | Oldest pending live delivery > 300 seconds for 5 minutes; > 900 seconds for 2 minutes pages | Warn/page respectively; inspect backoff, credentials and dead letters; do not bypass privacy or human approval |
| Outbox pressure | Pending count > 800 for 5 minutes; > configured readiness threshold for 1 minute | Warn/page; protect durable intake capacity and address downstream outage |
| Dead letters | Any new permanent/exhausted delivery | Operator review; retry only after cause is corrected |
| Storage/migrations | `/health/ready` 503 for 1 minute or migration failure | Page; `/health/live` distinguishes recovery from process failure |
| Audit integrity | Any `civicops.audit.integrity_failures > 0` or offline verifier failure | Immediate page; isolate affected partition, preserve evidence, use verified restore/export procedures |
| Missing signals | No health probe or metrics for 2 minutes during expected operation; `MetricsUnavailable` sampling failures for > 1 minute | Warn/page after confirming collector; retained gauge values do not establish a fresh audit sample |
| SLA review breaches | Any urgent review breach for 5 minutes; other breaches > 10 for 15 minutes | Municipal operations escalation; no automatic dispatch approval |

Example intake burn query requires collector mapping of ASP.NET HTTP duration counts and a synthetic probe series. Map valid intake routes and exclude expected rejected requests consistently; the application does not expose a Prometheus HTTP scrape endpoint itself:

```promql
sum(rate(http_server_request_duration_seconds_count{http_route=~"/api/incidents|/open311/v2/requests",http_response_status_code=~"5.."}[5m]))
/
clamp_min(sum(rate(http_server_request_duration_seconds_count{http_route=~"/api/incidents|/open311/v2/requests",http_response_status_code!~"4.."}[5m])), 0.001)
> 0.0144
```

For voice/chat, include the actual protected tool, simulated-chat and post-call route/result mapping and provider-independent synthetic form probes. The accepted-report counter alone is insufficient to measure availability because it cannot count failed attempts. Exporter/collector naming and route metadata must be verified before adopting these expressions.

At 50% intake budget consumption in the window, review incidents and defer discretionary risk. At 100%, freeze changes that do not restore reliability until the owner approves a recovery plan. Security and audit repair remain urgent. Review SLOs monthly with intake volume, missing measurements and provider outage evidence; track business impact separately from synthetic benchmark timing.

See [signals and operational health](OBSERVABILITY.md) and [backup/restore procedures](BACKUP_RESTORE.md) for investigation and recovery. Disaster recovery RPO/RTO objectives require scheduled backups and owner drills; passing a local restore smoke does not demonstrate cloud RPO/RTO.
