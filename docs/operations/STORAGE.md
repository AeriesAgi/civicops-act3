# Municipal storage

`CIVICOPS_STORAGE=file` is the default. The existing one-command demo remains a flushed, atomic JSON file store with an exclusive process lease. PostgreSQL is opt-in with `CIVICOPS_STORAGE=postgres` and `CIVICOPS_DATABASE_URL` or `CIVICOPS_DATABASE_URL_FILE`. Missing or invalid PostgreSQL credentials fail startup; CivicOps never silently switches a configured database to a separate file store.

Npgsql **10.0.3** is pinned (PostgreSQL License). Both Npgsql connection strings and `postgresql://` URLs are supported. URL query options are `sslmode` and `search_path`; use a connection string for other options. Mounted secrets use the same bounded round-5 provider. Connection strings and database exceptions are not returned by health or CLI output. Use TLS, a dedicated database and restricted database/network access in production. Connection usernames, passwords and certificate paths are owner secrets, never committed examples.

The repository's [Compose configuration](../../docker-compose.yml) forwards the optional storage/database, audit signing/export, provider-timeout/readiness and example `OTEL_*` settings to CivicOps. With no configuration it retains explicit synthetic demo mode, file storage, disabled signed exports and no external telemetry. PostgreSQL is an owner-supplied reachable database; this Compose file does not provision a database service. Host `localhost` addresses refer to the application container when used inside it, so select the database/collector hostname for that deployment.

The [Dockerfile](../../Dockerfile) copies `docs/schemas` into the build stage before publishing, matching the application's embedded-resource path so its portable JSON schemas are included. That path and its build-context inclusion were checked statically; container build/runtime validation remains owner-pending.

`CIVICOPS_DATABASE_URL_FILE` and `CIVICOPS_AUDIT_SIGNING_KEY_FILE` must name **container** paths, not unmounted host paths. The owner must provide read-only secret mounts through a private Compose override/secrets configuration and permit the non-root application user to read them. No secret values or secret mounts are supplied by the repository. Likewise, `CIVICOPS_AUDIT_EXPORT_DIR` needs an owner-provided writable persistent mount (or a directory beneath the existing `/app/Data` volume); the rest of the container filesystem is read-only. Audit artifacts require restricted retention/access independently of database storage. [Secret sources](../security/SECRETS.md), [signed audit exports](AUDIT_EXPORT.md) and [telemetry configuration](OBSERVABILITY.md) describe those boundaries. Docker/Compose validation is static; no container runtime was exercised on this host.

## Implemented persistence and transactions

PostgreSQL is authoritative; no database-backed store mirrors municipal data to local JSON. `civicops_documents` contains versioned, tenant-bound durable snapshots identified by logical paths: the tenant directory, incidents and their consent receipts, separately stored optional contacts, DSAR records, crews/workers and certification grants, training, service accounts, local bootstrap users, catalogue revisions, voice webhook receipts, simulated Zendesk request artifacts and simulated email messages. The exact serialized snapshot is retained to preserve historical authenticated state and migration compatibility. `civicops_incidents`, `civicops_audit` and `civicops_outbox` are transactionally maintained relational projections with tenant/id keys, unique intake/delivery deduplication keys and due-delivery indexes.

A PostgreSQL transaction and advisory transaction lock serialise durable reads/mutations across application instances. Nested stores join the same transaction. Every incident write commits its case snapshot, optional contact, chain entries and missing create/status/approved-communication intents together. Failures roll back the entire unit of work. Expected case and tenant versions are checked against fresh database state, so concurrent instances cannot overwrite a newer version or create duplicate idempotency receipts. Workflow drafting happens before the final storage transaction. Only requests that omit an expected version may tolerate intervening Zendesk delivery metadata: the store checks the complete unchanged case state, exact old audit prefix, delivery-only appended events and matching version advancement under its lock. It preserves the delivered ticket details and appends the prepared workflow events to the latest audit. Explicit expected versions and concurrent human, safety, consent or location changes still conflict. Historical audit entries are verified before mutation; the audit projection refuses replacement of an existing sequence with another hash. These controls do not prevent a database superuser from modifying data; signed exports and external immutable retention are separate controls.

Hosted outbox workers elect one polling leader per PostgreSQL schema with a session advisory lock on a dedicated connection. A peer uses `pg_try_advisory_lock` and skips a busy polling pass; releasing/losing the connection permits takeover on the next poll. Leadership uses a separate key from snapshot transaction locks. Outbox workers commit an attempt/lease before contacting a provider, release the snapshot database transaction during that call, and atomically commit the resulting case audit and delivery state afterward. The dedicated leadership connection remains held through that polling pass, including provider calls. Other instances cannot claim the same unexpired attempt, including direct processing outside the hosted worker. Lost responses retry using the existing provider event marker reconciliation. Per-case order and dead-letter blocking remain. A claim lease is at least the bounded request timeout plus ten seconds. External delivery cannot share a database transaction; downstream deduplication and reconciliation are still required. Leadership exclusion, release/handover and independent readiness are tested against a disposable PostgreSQL cluster.

The current locking strategy deliberately serialises all municipal snapshot operations in one database. It is tested for correctness across instances; horizontal throughput, maximum production dataset size and municipal load targets require owner measurement. Snapshots are read and serialized as complete documents. Tenant-filtered row-level repositories and narrower locks are a future scaling change, not implemented here. SSE notifications, staff sessions and rate-limit counters remain instance-local. The round-7 chart therefore refuses more than one active application replica; ingress affinity alone does not repair lost notifications or shared sessions/quotas. The [deployment guide](DEPLOYMENT.md) records the single-node tests, required cross-node work and safe rollout boundary. [Bounded loopback load results](LOAD_TEST_RESULTS.md) are synthetic measurements on a shared VM, not production capacity evidence.

## Embedded versioned migrations

SQL files in `products/civicops/Storage/Migrations` are embedded in the application. Startup applies pending migrations transactionally under an advisory lock **before** workforce/training initialization. The migration ledger stores each SQL SHA-256; changing an applied migration fails closed. Add a new version instead of modifying deployed SQL. The database role therefore needs schema DDL at startup; a production owner may run the explicit migration command with a temporary migration role before running an already migrated application with a narrower role.

```bash
CIVICOPS_STORAGE=postgres dotnet run --project products/civicops -- --migrate
```

`Search Path` optionally selects one simple schema name; startup creates it if absent. Default is PostgreSQL's configured search path. Complex/multiple search paths are rejected. Test factories use unique disposable schemas and drop them after each fixture.

## File-to-PostgreSQL import

Stop the source application and take a verified backup. Set the target PostgreSQL connection through a secret source and select a separate `CIVICOPS_DATA_DIR` logical root. The target must contain **zero durable documents**; import never merges or overwrites existing municipal data.

```bash
CIVICOPS_STORAGE=postgres dotnet run --project products/civicops -- \
  --import-file-store /absolute/path/to/stopped-file-data
```

The importer owns the source process lease, rejects symlinked selected data and invalid tenant partition names, and imports an allow-list of durable documents and simulated delivery artifacts. It verifies municipal incident, workforce, training, identity, catalogue and DSAR integrity inside one target transaction. Invalid JSON, a damaged authenticated snapshot or a nonempty target aborts. Historical audit hashes are preserved; legacy migration events may extend the chain. Unrecognised files, lock files, temporary files, exported bundles and secrets are excluded. Keep the source backup until the owner validates migrated counts, tenancy and permissions. Import performs no provider delivery.

## Shared contracts and disposable PostgreSQL

Existing persistence, tenant-isolation, workforce, training and service-account/bootstrap contracts use one shared backend fixture. File-only filesystem corruption/recovery fixtures still run against files. When `CIVICOPS_TEST_DATABASE_URL` is set, each shared contract also executes against PostgreSQL. Added contracts cover case/audit/intent atomicity, rollback, concurrent independent instances and verified import/nonempty rejection.

```bash
dotnet test -c Release --no-build
./scripts/with_ephemeral_pg.sh dotnet test -c Release --no-build
```

The wrapper uses PostgreSQL 17 binaries, the current unprivileged user, `$TMPDIR`, a random loopback port, a private socket directory, 32 MB shared buffers and at most 30 connections. It exports both database URLs and `CIVICOPS_EPHEMERAL_PG=1`, then tears the cluster down on success or failure. Trust authentication is confined to the disposable loopback test cluster. It never touches the system cluster. CI includes an isolated PostgreSQL service job; remote CI execution remains owner-pending until publication.

Backups and actual restore drills are described in [BACKUP_RESTORE.md](BACKUP_RESTORE.md). Npgsql's native tracing is configured with fixed command names; the telemetry processor removes query text and exception payloads. See [official Npgsql tracing documentation](https://www.npgsql.org/doc/diagnostics/tracing.html).
