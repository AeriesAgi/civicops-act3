# CivicOps observability

The application implements OpenTelemetry traces, metrics and logs plus structured JSON console logs. Local tests use fake providers, in-memory telemetry and an actual loopback OTLP HTTP collector drill (`scripts/smoke_telemetry.sh`); a deployed collector, dashboards, alert routing, live accounts and cloud exporters remain owner-pending. The synthetic demo defaults to telemetry off and never exports to an external collector. `CIVICOPS_TELEMETRY=console` enables local inspection. Demo mode suppresses `otlp` even when an endpoint is supplied.

Both file and PostgreSQL collector drills passed locally. The PostgreSQL drill checks native Npgsql command spans and operation/connection-pool metrics received by the real OTLP exporter sink, fixed SQL span names, removed URL/SQL attributes, and synthetic database-password/provider/PII redaction. It rejects PostgreSQL mode unless the disposable-cluster wrapper supplies its marker and matching loopback test database URL. The sink uses HTTP/protobuf on loopback only; cloud collector delivery remains unverified.

```bash
./scripts/smoke_telemetry.sh
./scripts/with_ephemeral_pg.sh ./scripts/smoke_telemetry.sh postgres
```

## Configure a protected deployment

Set `CIVICOPS_TELEMETRY=otlp`, `OTEL_SERVICE_NAME=civicops`, optional `OTEL_SERVICE_VERSION`, `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_PROTOCOL=grpc|http/protobuf`. Standard `OTEL_EXPORTER_OTLP_HEADERS`, per-signal `OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_{ENDPOINT,HEADERS,PROTOCOL,TIMEOUT}`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_METRIC_EXPORT_INTERVAL` and `OTEL_SDK_DISABLED` configure the SDK/exporter. Use the collector's supported transport; HTTP/protobuf normally uses port 4318 and gRPC 4317. Keep collector credentials outside the repository. Export failure does not gate intake/readiness. The owner must restrict collector access, retention and egress.

Call order wraps JSON-console and OpenTelemetry log providers with the existing secret/PII redactor. Structured fields and scopes survive redaction. Logs include correlation/trace/span IDs, method, route template, response status and elapsed time; request/response bodies are never logged by the request middleware. PII minimisation is best effort and retains the round-5 limits: unlabeled names and arbitrary prose are not reliably recognised. Do not add resident identifiers as metric labels, span attributes or resource attributes.

The application accepts a bounded, non-PII `X-Correlation-ID`; invalid values receive a generated opaque ID. W3C `traceparent` follows ASP.NET Core and provider HTTP requests. Response headers and ProblemDetails echo trace/correlation IDs. Logs omit raw exception content, provider payloads and secrets. Span processing redacts string tags and removes full URL, path/query, SQL statement and exception message/stack attributes. Parameterised Npgsql command/batch spans use fixed operation names; SQL parameter values are not recorded. Third-party exception events and operator-supplied resource attributes need collector-side scrubbing as an additional boundary.

## Signals and dashboards to build

| Signal | Implemented source and meaning |
|---|---|
| Incoming HTTP | ASP.NET Core instrumentation; request duration, status and route |
| Outgoing HTTP | HttpClient instrumentation, propagated W3C context |
| PostgreSQL | Native `Npgsql` activity source and meter; fixed command/batch names |
| Agent pipeline | `intake.agent_pipeline`, `agent.IntakeAgent`, `agent.TriageAgent`, `agent.DuplicateAgent`, `agent.DispatchAgent`, `agent.CommsAgent` spans; fallback flags/reasons |
| Providers/delivery | `provider.ai`, `provider.elevenlabs`, `provider.zendesk`, `outbox.delivery` spans; operation and simulated/live delivery labels |
| `civicops.intake.total` | New reports durably accepted, all channels; idempotency replays excluded |
| `civicops.triage.duration` | Seconds for extraction plus triage before downstream agent steps |
| `civicops.agent.decisions`, `civicops.ai.fallbacks` | Decision counters; bounded agent-name label; fallback ratio |
| `civicops.outbox.attempts`, `civicops.outbox.delivery.duration` | Attempts and creation-to-delivery seconds, including retry delay, labelled `mode` |
| `civicops.outbox.backlog`, `civicops.outbox.oldest_age` | Pending count and oldest pending age; sampled every 15 seconds across municipalities |
| `civicops.sla.breaches` | Open cases beyond the catalogue review target; this is not an arrival promise |
| `civicops.audit.integrity_failures` | Number of sampled municipality chains failing verification |

Build intake availability/latency, triage/fallback, queue/dead-letter delivery, storage/connection-pool, SLA review and audit-integrity dashboards. Keep demo and live delivery panels separate. Zero requests means no availability sample; empty latency histograms provide no p95. Gauges update every 15 seconds after a successful storage sample and retain previous values during storage outages. The exporter continues publishing those old values, so series timestamps alone do not establish sample freshness. Alert on the `MetricsUnavailable` log event and failed storage readiness; a last-success timestamp signal requires owner collector mapping or an additional application metric. Alert separately on missing telemetry. The audit sampler verifies stored chains, so large installations must measure its overhead and choose a separate integrity schedule before production rollout.

Example PromQL assumes the collector's Prometheus exporter translates dots to underscores, adds unit suffixes and exposes cumulative counters/histograms. Verify names in the chosen collector; these examples are documented-only dashboards:

```promql
sum(rate(civicops_intake_total[5m]))
histogram_quantile(0.95, sum by (le) (rate(civicops_triage_duration_seconds_bucket[5m])))
sum(rate(civicops_ai_fallbacks_total[5m])) / clamp_min(sum(rate(civicops_agent_decisions_total[5m])), 0.001)
max(civicops_outbox_backlog)
max(civicops_outbox_oldest_age_seconds)
histogram_quantile(0.95, sum by (le) (rate(civicops_outbox_delivery_duration_seconds_bucket{mode="live"}[15m])))
max(civicops_sla_breaches)
max(civicops_audit_integrity_failures)
```

Example KQL for an owner-configured Azure Monitor pipeline (table/column mapping must be confirmed in that deployment):

```kusto
AppDependencies
| where TimeGenerated > ago(1h) and Name == "agent.TriageAgent"
| summarize p95TriageStepMs=percentile(DurationMs, 95) by bin(TimeGenerated, 5m)

AppTraces
| where TimeGenerated > ago(1h)
| where tostring(Properties.CorrelationId) == "<opaque request correlation>"
| project TimeGenerated, Message, OperationId, Properties
```

The KQL dependency query measures the triage step alone; the custom triage histogram includes extraction and is the SLO signal. Never copy resident content into a correlation filter.

## Health and recovery

`/health/live` returns HTTP 200 when the process serves requests. `/health/ready` checks storage access, migration completion and total pending outbox count against `CIVICOPS_OUTBOX_READY_MAX` (default 1000, range 0–10000). PostgreSQL readiness reads the current global queue projection without holding a delivery transaction. File readiness reads atomically published durable queue snapshots for every configured municipality without waiting on the provider queue gate, including partitions not yet visited by the worker. Storage/migration/backlog failures return 503. Provider simulation, missing configuration, unverified status, failures and open circuits return `status: degraded` with HTTP 200 while intake remains available. No startup/health probe calls external providers. `/healthz` retains its prior response contract.

Provider circuits open after three consecutive transient failures for 30 seconds and admit one recovery probe. Permanent request rejection does not contribute to opening. The AI has a shared decision timeout (existing `CIVICOPS_LLM_TIMEOUT_MS`), at most two attempts and deterministic fallback. ElevenLabs/Zendesk operations have `CIVICOPS_PROVIDER_TIMEOUT_MS` (default 5000, bounded 250–20000). The durable outbox applies capped exponential backoff with 0–20% positive jitter, preserves case order and exposes dead letters. Cancellation of the caller does not count as a provider outage. The resident UI displays an accessible degraded/unverified provider banner and preserves report-form intake.

[Static Kubernetes probe example](../../deploy/k8s/civicops-demo.yaml) uses startup/readiness checks at `/health/ready` and liveness at `/health/live`. It is a synthetic deployment with ephemeral data, an owner-supplied image and no production ingress; only static YAML validation was performed. A production rollout needs durable PostgreSQL, secret mounts, reviewed resources/image digest, HTTPS ingress and an approved municipality configuration.

Primary implementation references: [OpenTelemetry .NET OTLP configuration](https://github.com/open-telemetry/opentelemetry-dotnet/tree/core-1.19.1/src/OpenTelemetry.Exporter.OpenTelemetryProtocol), [Npgsql tracing](https://www.npgsql.org/doc/diagnostics/tracing.html), [Npgsql 10 metric names](https://www.npgsql.org/doc/diagnostics/metrics.html). OpenTelemetry packages are pinned and Apache-2.0 licensed; Npgsql uses the [PostgreSQL licence](https://www.nuget.org/packages/Npgsql/10.0.3).
