# 0001 — Selectable file and PostgreSQL storage

- Status: accepted; production database operation remains owner-pending.
- Date recorded: 2026-10-08 (SAST).
- Scope: authoritative persistence, migration and the deployment scaling boundary.

## Context and Problem Statement

CivicOps must run a synthetic demonstration with one command and no infrastructure configuration, while a municipal deployment needs durable transactional case, contact, audit and delivery-intent writes. Identity, workforce, training, catalogue, privacy and provider records must follow the same storage choice. Changing only the incident store would leave inconsistent recovery and tenant guarantees.

## Decision Drivers

- Preserve existing demo behavior and historical authenticated snapshots.
- Fail closed on corruption, duplicate ownership and stale optimistic versions.
- Make database migrations and file import reviewable and recoverable.
- State the difference between shared durable state and a fully distributed application.

## Considered Options

- File storage only, including production.
- PostgreSQL only, requiring a database for the demo.
- A shared persistence boundary with file as the default and PostgreSQL as an explicit option.

## Decision Outcome

Choose the shared boundary in [DurableStorage.cs](../../products/civicops/Services/DurableStorage.cs), selected by `CIVICOPS_STORAGE=file|postgres`. All durable stores use it. File mode keeps flushed temporary snapshots and same-filesystem atomic replacement through [AtomicJsonFile.cs](../../products/civicops/Services/AtomicJsonFile.cs); `DataDirectoryLease` prevents another process from owning the same directory.

PostgreSQL is authoritative rather than a JSON mirror. Versioned documents preserve snapshot compatibility; relational incident, audit and outbox projections provide transactionally maintained indexes. Nested stores join a transaction, fresh reads enforce expected versions, and a database advisory transaction lock serializes snapshot mutations. The case unit of work includes optional contact, audit and missing delivery intents. [Embedded SQL](../../products/civicops/Storage/Migrations/001_durable_entities.sql) is checksum-verified under a migration lock; changing applied SQL fails closed. [StorageCommand.cs](../../products/civicops/Services/StorageCommand.cs) provides explicit migration and verified import into an empty database.

## Consequences

The file demo needs no service dependency. PostgreSQL supplies tested atomicity and cross-instance durable concurrency. File mode has no directory fsync or transaction across separate files; recoverable intents/reconciliation remain necessary. PostgreSQL still reads and serializes complete documents, and its coarse lock favors correctness over write throughput.

Shared storage does not share staff sessions, quotas or SSE. The [Helm deployment](../../deploy/helm/civicops/templates/deployment.yaml) and chart validation preserve one active application replica with Recreate. Manual scaling or another release can bypass that boundary, so the owner must enforce it. Multi-node sessions/quotas/fan-out, narrower locks and production-size load measurements need further engineering. Managed database TLS, privileges, backup scheduling and PITR require owner configuration.

## Confirmation

Implemented and tested locally: [PostgresStorageTests.cs](../../tests/CivicOps.Tests/PostgresStorageTests.cs) covers atomicity, rollback, independent-instance idempotency/version conflicts and import; [PersistenceAuditTests.cs](../../tests/CivicOps.Tests/PersistenceAuditTests.cs) protects file integrity. Shared [backend contracts](../../tests/CivicOps.Tests/StoreBackends.cs) run against file and disposable PostgreSQL. [ScalingBoundaryTests.cs](../../tests/CivicOps.Tests/ScalingBoundaryTests.cs) covers worker leadership independently of the application pool. [smoke_restore.sh](../../scripts/smoke_restore.sh) verifies both recovery paths.

See [storage operations](../operations/STORAGE.md), [backup/restore](../operations/BACKUP_RESTORE.md) and [deployment limits](../operations/DEPLOYMENT.md). Docker/Kubernetes runtime, managed database operation and production throughput remain unverified live.
