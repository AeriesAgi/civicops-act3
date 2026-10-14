# CivicOps deployment and scaling

Round 7 supplies production container and deployment **assets validated statically**, plus tested trusted-proxy handling and outbox coordination. No container was built or run, no Kubernetes/Compose stack was deployed and no municipal infrastructure was accessed on this host. Real HTTPS/SSO, image scanning/signing, network policies, storage permissions, backups and capacity remain owner validation before a resident pilot. Keep the [POPIA controls](../privacy/POPIA.md), [security review](../security/ASVS_L2.md) and [recovery guide](BACKUP_RESTORE.md) in that approval.

## Container and local compatibility

The root [Dockerfile](../../Dockerfile) publishes in .NET SDK **10.0.401 Ubuntu Noble** and runs ASP.NET **10.0.12 noble-chiseled-extra**, both pinned to registry manifest digests resolved read-only on 8 October 2026. The extra variant includes ICU/timezone support for English, Afrikaans and isiZulu. The runtime has no package manager, shell or curl, runs UID/GID **1654**, exposes unprivileged port **8080** and uses a separate package-free [.NET readiness helper](../../deploy/container/HealthProbe/Program.cs) for Docker HEALTHCHECK. The helper accepts only loopback HTTP, refuses redirects, has a two-second timeout and exits nonzero for unavailable/unready storage. [Microsoft's chiseled image guide](https://github.com/dotnet/dotnet-docker/blob/main/documentation/ubuntu-chiseled.md) describes the underlying image.

Mount writable `/app/Data` and `/tmp`; keep the root filesystem read-only, drop all capabilities and disable privilege escalation. `/app/Data` holds durable file data or the logical PostgreSQL path and optional audit exports; PostgreSQL municipal state stays in the database. Restrict audit export mounts and retention separately. DataProtection keys are explicitly ephemeral, matching the existing in-memory session contract; cookies, CSRF tokens and in-flight OIDC handshakes expire on restart. Staff must reload and sign in again. The application needs no writable `$HOME` key directory. Distributed sessions remain unimplemented.

`.dockerignore` excludes credentials, filled env files, private key formats, data, test artifacts, tool installations and screenshots while retaining embedded schemas. No secret enters a Docker build argument/layer. Owners should build a versioned image, scan it, retain an SBOM and deploy its immutable digest; the chart's `civicops:round7` is a local build name, not an image hosted by this repository. Refresh pinned bases through a reviewed update and rerun all gates.

The existing root Compose file and zero-configuration command remain available:

```bash
CIVICOPS_DEMO_MODE=true dotnet run --project products/civicops --urls http://localhost:5080
```

## Compose with PostgreSQL and optional dependencies

[deploy/compose/docker-compose.yml](../../deploy/compose/docker-compose.yml) includes CivicOps, PostgreSQL **17.11-bookworm**, an optional OpenTelemetry collector **0.161.0** under `observability`, and an optional digest-pinned ROCm vLLM under `amd-vllm`. It defaults to non-demo PostgreSQL and requires a private database password/connection string. PostgreSQL has SCRAM host authentication, a persistent volume, a health-gated app startup and no host port. Only the app's HTTP port is published on loopback; put a trusted HTTPS reverse proxy in front of it. The existing root Compose file retains its explicit synthetic file demo.

Provide a private absolute env-file **outside the repository**. Set `CIVICOPS_COMPOSE_ENV_FILE` to that path and pass the same path with `--env-file`: Compose interpolation and container env-file loading are separate operations, as explained in [Docker's environment documentation](https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/). The repository contains only blank inventories. Private configuration needs `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` and `CIVICOPS_DATABASE_URL` with host `postgres`, plus approved staff bootstrap/OIDC settings. This local PostgreSQL service is not a managed HA/PITR database. Stop it and validate backups before maintenance; use an owner override to replace it with managed PostgreSQL and remove the local dependency.

Owner execution on a Docker-capable machine:

```bash
docker compose --env-file "$CIVICOPS_COMPOSE_ENV_FILE" -f deploy/compose/docker-compose.yml config --quiet
docker compose --env-file "$CIVICOPS_COMPOSE_ENV_FILE" -f deploy/compose/docker-compose.yml up --build -d
```

Avoid printing a resolved Compose configuration: it can contain interpolated secrets. Use an owner-only override with read-only Docker secret mounts when selecting `*_FILE` sources; each path must refer to the container, and UID 1654 must be able to read it. Environment configuration remains supported but is visible to container administrators.

With `--profile observability`, set `CIVICOPS_TELEMETRY=otlp` and `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` privately. The [example collector](../../deploy/compose/otel-collector.yml) exports sanitized traces/logs to its logs and metrics to private port 8889; it supplies no cloud backend, alerts or retention policy. Demo mode intentionally suppresses external OTLP. Without these settings telemetry stays off. For the GPU profile, configure the private app model URL/key and follow [AMD_VLLM.md](AMD_VLLM.md); the profile maps `/dev/kfd` and `/dev/dri` and requires actual compatible AMD hardware. Missing model/provider settings preserve deterministic or simulated operation.

## Kubernetes and Helm

The [chart](../../deploy/helm/civicops) includes Deployment startup/liveness/readiness probes, requests/limits, read-only non-root security contexts, Service, TLS Ingress, ConfigMap, existing Secret references, PDB, optional HPA, NetworkPolicy, a pre-install/pre-upgrade migration Job and optional AMD vLLM resources. Storage mode/data root/runtime environment/HTTP port are fixed explicit Deployment environment values, taking precedence over Secret/ConfigMap envFrom; reserved ConfigMap overrides are rejected. The chart does not install a database, ingress controller, TLS issuer, external-secrets controller, metrics operator or AMD driver/device plugin. These are owner infrastructure dependencies.

Provide a private values file outside the repository with the built `image.repository`/`image.digest`, `existingSecret`, `ingress.host`, `ingress.tlsSecretName`, explicit trusted ingress proxy ranges, allowed host names, network selectors/CIDRs and resources appropriate to the municipality. Existing Secret `civicops-runtime` must contain `CIVICOPS_DATABASE_URL`; provider/API/identity keys use their standard environment names in that same Secret. Never pass secret values with Helm `--set` or put them in ConfigMap/values. An ExternalSecrets controller may provision the referenced Secret from a municipal secret manager; controller setup, access policy and rotation are owner work. Chart resources contain names/references only.

```bash
helm lint deploy/helm/civicops --strict -f /run/civicops/values.yaml
helm template civicops deploy/helm/civicops -n civicops -f /run/civicops/values.yaml
helm upgrade --install civicops deploy/helm/civicops -n civicops --create-namespace \
  -f /run/civicops/values.yaml --wait --timeout 5m
```

The TLS Ingress disables nginx SSE buffering and extends read timeout. Owners using another ingress controller must configure equivalent behavior, request limits and HTTPS redirects. Set `CIVICOPS_TRUSTED_PROXY_NETWORKS` to at most 16 comma-separated IPs/CIDRs of the **direct ingress proxy**, never all clients or `0.0.0.0/0`. The app consumes one matching `X-Forwarded-For`/`X-Forwarded-Proto` hop before authentication, quota calculation and HSTS; untrusted, incomplete or asymmetric headers cannot alter client address/scheme. Host forwarding is not accepted. Set `AllowedHosts` to the actual municipal hostname, preserve that Host at the ingress and configure the ingress to replace forwarded headers supplied by clients. Do not enable trust-all automatic forwarding. [Microsoft's proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0) explains these boundaries. Test the actual OIDC HTTPS callback, cookie flags, HSTS and spoofed forwarding on the deployed ingress before enabling resident access.

NetworkPolicy permits the selected ingress namespace on port 8080; DNS; selected PostgreSQL pods or explicit database CIDRs on 5432; selected collector pods on 4317/4318; and optional private vLLM on 8000. Provider/model downloads require explicit `httpsEgressCIDRs`. Resolve address rotation through an owner-controlled egress proxy/CNI policy rather than permitting every destination. CoreDNS labels and namespace selectors must match the actual cluster. Enforcement depends on the chosen CNI and has not been deployed here.

Optional ServiceMonitor targets an existing **collector Prometheus exporter service** in `serviceMonitor.collectorNamespace`, using the owner's labels and named `prometheus` port. CivicOps exports OTLP and has no app `/metrics` endpoint. Install the Prometheus Operator CRD and configure collector port 8889/network access before enabling this template. The repo vendors its official v0.87.1 schema solely for strict static validation; it never installs that CRD. Add external collector network policy/Prometheus access in the monitoring deployment.

## One active instance and shared durable state

The HTTP handlers store municipal state in shared PostgreSQL and need no local durable incident files in PostgreSQL mode. The app is **not yet fully stateless**: SSE subscriptions, staff sessions, CSRF protection and per-process quota counters are local. Both PostgreSQL and file Helm configurations therefore refuse `replicaCount > 1`, and HPA refuses a maximum above one. A valid optional HPA renders with min/max one, but does not deliver horizontal scaling. The Deployment uses `Recreate` to avoid overlapping instances during upgrades. These template safeguards do not provide a global HTTP singleton lease: the owner must prevent manual scaling and a second active release against PostgreSQL.

File storage additionally owns an exclusive process lease on `CIVICOPS_DATA_DIR`; a second process using the same directory fails closed. It requires a persistent RWO volume in this chart and must never run multiple replicas with independent file volumes. Snapshot replacement is individually atomic, not a database transaction. Keep backups, underlying filesystem permissions and restoration drills in the [storage guide](STORAGE.md).

The tested SSE implementation has tenant-filtered subscriptions, training filtering, a 16-event bounded queue which drops oldest events (including training events), and explicit disconnect cleanup. Messages contain event names only; clients must refetch authorized authoritative data and reconcile after reconnect or missed events. It has no PostgreSQL LISTEN/NOTIFY, distributed replay or cross-node fan-out. Ingress affinity alone cannot deliver an update produced by another replica to a local subscriber. Implement committed tenant/event-name notification plus reconnect/replay behavior, distributed sessions/DataProtection and quota coordination before relaxing chart guards; verify two-node isolation and failover with real clients first. This is documented future work, not an implemented multi-node feature.

The PostgreSQL outbox worker elects a polling leader with a dedicated **session advisory lock scoped to the configured database schema**. A peer skips that pass, and leadership can transfer when the connection is disposed or lost. Leadership uses a separate, explicitly named **unpooled/nonmultiplexed** data source, so even an application pool of one remains available for state transactions. Each active leadership attempt needs one additional physical database connection outside that pool; budget it in PostgreSQL connection limits. Application state also disables configured multiplexing to preserve physical transaction ownership. Both data sources use safe trace/pool names. The leader connection stays open while bounded provider calls run; the municipal snapshot transaction does not. Per-item transactional leases/version checks remain the correctness fence, and delivery/reconciliation is at least once. Database failure is logged safely and polling retries. Do not add an idle-session timeout shorter than a complete polling pass/provider budget without revalidating leadership behavior. Advisory locks cannot make an external write transactional.

## Migrations, rollout and rollback

Embedded migration files are immutable after application: version/checksum mismatches fail startup. Add a new numbered migration and follow expand/contract changes. The Helm hook runs `dotnet CivicOps.dll --migrate` with a referenced Secret before installing/upgrading the app. It uses explicit environment values rather than a ConfigMap that does not yet exist during a pre-install hook. A weight -10 hook creates a migration-only DNS/PostgreSQL egress NetworkPolicy before the weight -5 Job, including in namespaces with default-deny policies. That policy stays until the next hook recreates it: deleting a successful policy hook immediately would block the later Job. Helm does not track hook resources for uninstall; owners must remove the `<release>-civicops-migration` NetworkPolicy when retiring a release. All existing Secrets, including those populated by ExternalSecrets, must be ready **before** running Helm; a Secret created by a normal resource after pre-install is too late. A dedicated `migration.existingSecret` may provide a DDL role; the runtime Secret then uses the approved narrower role after startup has verified the ledger. The migration role and runtime role must use the **same schema/search path and `/app/Data` logical root**. Test actual least-privilege SQL grants on the owner database. The app still verifies/applies migrations at startup under its existing transaction advisory lock.

For the supported single-active-instance rollout:

1. Take and verify a backup; record schema versions, current image digest, audit heads and the rollback image. Test the new image against an isolated synthetic database.
2. Apply only backward-compatible expansions: additive tables/columns and tolerated defaults. Verify both old/new application contracts against the expanded schema. Never edit an applied SQL file.
3. Announce a maintenance window, finish/review in-flight changes and drain long-lived SSE/requests. Run the migration hook and deploy using `Recreate`; brief unavailability and staff reauthentication are expected.
4. Verify live/ready probes, authenticated municipality isolation, one synthetic consented intake, human workflow, audit verification, SSE refresh and outbox backlog. Restore traffic only after those checks.
5. Retain the old image and backup through observation. Contract/removal migrations belong in a later release after the old version/rollback path is retired.

A blue/green deployment is presently a **staged cutover**, not zero-downtime: validate green on an isolated synthetic database, quiesce and stop blue, point green at the shared production database only after blue has stopped, then switch the Service/Ingress and verify. Never run blue and green as simultaneously active resident desks against shared state while SSE/sessions/quotas remain local. Drain rollback similarly. Do not duplicate a file-store volume or run file-backed colors together.

True rolling updates with `maxUnavailable=0`/surge replicas and zero-downtime blue/green are documented-only until distributed behavior is implemented and tested. After that work, retain expand/contract compatibility, migrate before switching traffic, use independent readiness and outbox leadership, drain old SSE connections, observe errors/backlog and roll back the image if needed. The current chart intentionally does not expose a rolling strategy bypass.

Image rollback uses an application version compatible with the expanded schema. Helm rollback does not undo database migrations. A destructive schema/data rollback requires a stopped application and separately approved backup/PITR restoration, with recorded data-loss implications; follow the tested [restore safety controls](BACKUP_RESTORE.md). Default PDB `minAvailable: 1` blocks voluntary eviction of the only app pod. For a planned drain, deliberately lower/disable it within the maintenance window and restore it afterward; the PDB does not provide a second instance or prevent node failure.

## Reproducible local evidence

```bash
./scripts/validate_deployment.sh
```

The script downloads checksum-pinned Helm **3.19.0**, kubeconform **0.7.0** and hadolint **2.14.0** into `$TMPDIR/bin`; pinned PyYAML **6.0.3** stays in temporary tooling. It runs hadolint, three strict Helm lints, four `helm template` → `kubeconform -strict` variants against Kubernetes **1.34.0**, eight rejected unsafe configuration checks, Compose/YAML semantic checks and five actual loopback health-helper checks. Default/file/features/AMD renders validate **7/6/10/10 resources**, all valid, zero skipped. Trusted forwarding has **24 regression cases**, including rejection of IPv6 ranges covering every IPv4-mapped peer; ephemeral identity protection also has a restart-boundary regression. CI runs the same static deployment gate and OpenAPI lint, and installs pinned axe-core alongside Playwright only for browser QA. Remote CI, container vulnerability scanning, actual read-only container startup, cluster admission and production operations remain owner-pending.
