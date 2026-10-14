# 0010 — Redacted OpenTelemetry and explicit operational health

- Status: accepted; production collector, alerts and achieved SLOs remain owner-pending.
- Date recorded: 2026-10-08 (SAST).
- Scope: traces, metrics, logs, probes and outage diagnosis.

## Context and Problem Statement

Operators need to distinguish storage failure from a provider outage, trace a request through bounded reasoning/delivery and detect integrity/backlog problems. Sending resident text, SQL or provider secrets into telemetry would create another sensitive data store. A demo must not export to an external collector implicitly.

## Decision Drivers

- Use a portable observability contract rather than a mandatory cloud vendor.
- Preserve useful request correlation without logging operational payloads.
- Separate process liveness, storage/backlog readiness and degraded providers.
- Make gaps in sample freshness, dashboards and service commitments explicit.

## Considered Options

- Plain unstructured logs only.
- A mandatory proprietary monitoring SDK/backend.
- OpenTelemetry traces/metrics/logs plus redacted JSON console logs and local/off modes.

## Decision Outcome

Choose [CivicOpsTelemetry.cs](../../products/civicops/Services/CivicOpsTelemetry.cs) with pinned OpenTelemetry packages and Npgsql's native signals. `CIVICOPS_TELEMETRY=off|console|otlp` configures export; standard `OTEL_*` settings select the collector/transport. No configured collector defaults to off; explicit demo suppresses external OTLP even if an endpoint is present. Export failure does not gate intake or readiness.

Instrumentation covers incoming/outgoing HTTP, database commands, agent decisions/fallbacks, delivery attempts and latency. [OperationalHealth.cs](../../products/civicops/Services/OperationalHealth.cs) supplies `/health/live`, `/health/ready` and compatible `/healthz`. Readiness checks storage, migrations and durable pending queues without provider calls; simulated, unverified and failed providers are reported as degraded while deterministic intake can remain available. [ProviderCircuit.cs](../../products/civicops/Services/ProviderCircuit.cs) describes bounded failures/recovery separately from storage readiness.

The correlation middleware accepts only bounded non-PII identifiers, propagates W3C context and echoes trace/correlation IDs. Logging preserves structured fields/scopes through [PiiRedactor.cs](../../products/civicops/Services/PiiRedactor.cs) and secret redaction. Request bodies are not logged. Span processing removes raw URL/path/query, SQL text and exception payload attributes; Npgsql span names are fixed. Resident identifiers must not become metric labels or operator resource attributes.

Operational sampling exposes backlog/oldest age, SLA review breaches and audit failures. [OperationalMetrics.cs](../../products/civicops/Services/OperationalMetrics.cs) computes audit-derived milestone medians with sample counts and honest digital-channel proxies; incomplete milestones provide no latency sample.

## Consequences

One signal contract can feed an owner-selected collector and backend. Best-effort redaction cannot reliably identify every name/free-text disclosure, and collector-side scrubbing/access/retention is still required. Gauges retain their previous values when storage sampling fails; exporter timestamps do not prove fresh samples. Operators must alert on storage readiness, `MetricsUnavailable` and missing telemetry. A last-success metric or owner mapping remains additional work.

PromQL/KQL examples, dashboards, paging routes and error-budget policy are documented proposals rather than deployed controls. Proposed [SLOs](../operations/SLO.md), catalogue targets and short shared-VM tests are not achieved availability/capacity commitments. The owner must approve objectives and validate observability end to end in production.

## Confirmation

Implemented and tested locally: [ObservabilityResilienceTests.cs](../../tests/CivicOps.Tests/ObservabilityResilienceTests.cs) checks redaction/correlation, bounded circuits, cancellation and unavailable/slow providers. [smoke_telemetry.sh](../../scripts/smoke_telemetry.sh) sends real traces, metrics and logs to a loopback OTLP sink on file and disposable PostgreSQL, including native Npgsql signal/redaction checks. Cloud exports, production alerts and incident response exercises remain unverified live. See [observability operations](../operations/OBSERVABILITY.md).
