# Backup and restore

Both backends have implemented, locally tested recovery tooling. The scripts create a new backup directory with a payload and `manifest.json` containing backend, timestamp, byte length and SHA-256. Restore verifies the entire payload before changing an empty target. Checksums detect accidental corruption; they do not authenticate a maliciously replaced backup and manifest. Protect them together with access control and immutable/off-site copies. Backups contain municipal records and optional contact data; treat them as restricted data. Never commit them.

## File backend

Stop CivicOps before backup or restore. The script takes the same exclusive file lock used by .NET and rejects an active application. It archives every durable file, including tenant partitions, contacts, DSAR, accounts, training, audit and integration artifacts. It excludes process locks and unfinished `.tmp` files and rejects symlinks/special files. Restore accepts only regular archive entries, rejects traversal/duplicates/links, extracts into staging, and requires an empty directory.

```bash
export CIVICOPS_STORAGE=file
export CIVICOPS_DATA_DIR=/absolute/private/civicops-data
./scripts/backup.sh /absolute/private/backups/new-backup
# Stop the application and preserve the damaged directory separately first.
# Select a new empty data directory; no automatic wipe is performed.
export CIVICOPS_DATA_DIR=/absolute/private/restored-data
./scripts/restore.sh /absolute/private/backups/new-backup
```

Restart with the same configuration, check `/health/ready`, and verify `/api/audit/verify` with an authorised auditor session. A backup of one file-store data directory covers every tenant in it. File backup is unavailable while the application is running; scheduling must include a short maintenance window. File mode retains its single-process boundary.

## PostgreSQL backend

The scripts use PostgreSQL 17 `pg_dump --format=custom --no-owner --no-acl`; its MVCC snapshot supports backup while CivicOps is running. Restore uses `pg_restore --single-transaction --exit-on-error` into an empty database. Stop **all** instances and create/select an empty target before restore. Database ownership, roles, grants, TLS settings and server configuration are deployment configuration and are not included in this application backup. Reapply reviewed least-privilege grants after restore.

```bash
export CIVICOPS_STORAGE=postgres
# Obtain the URL from your private secret source; do not place values in shell history.
export CIVICOPS_DATABASE_URL_FILE=/absolute/private/secrets/database-url
./scripts/backup.sh /absolute/private/backups/new-pg-backup
# Point the mounted URL file at a separately provisioned empty recovery database.
./scripts/restore.sh /absolute/private/backups/new-pg-backup
```

`CIVICOPS_DATABASE_URL` or `_FILE` supports a PostgreSQL URL or an Npgsql connection string with explicit host, database and username. Credentials are passed to libpq through the process environment and never printed or put in command arguments. `_FILE` takes precedence and fails closed. `CIVICOPS_PG_BIN` selects an absolute client binary directory; default `/usr/lib/postgresql/17/bin`, then commands on PATH. PostgreSQL restore includes all CivicOps schemas, migrations and durable documents. Applications validate the embedded migration checksums at startup.

## Optional encryption

Implemented: set `CIVICOPS_BACKUP_AGE_RECIPIENT` to a reviewed **public** age recipient. If `age` is unavailable, backup fails closed rather than producing a cleartext substitute. Restore requires `age` and `CIVICOPS_BACKUP_AGE_IDENTITY_FILE`, an absolute private identity file kept outside the repository and separately from backups. The checksum checks the encrypted payload's bytes; age supplies authenticated encryption. Keep recovery keys in an independently recoverable secret system. Identity rotation and key loss exercises are owner responsibilities.

```bash
export CIVICOPS_BACKUP_AGE_RECIPIENT   # Supply through private operator configuration.
./scripts/backup.sh /absolute/private/backups/encrypted-backup
export CIVICOPS_BACKUP_AGE_IDENTITY_FILE=/absolute/private/secrets/recovery-identity
./scripts/restore.sh /absolute/private/backups/encrypted-backup
```

GPG is an alternative documented operator workflow: encrypt the completed payload/manifest bundle with an existing reviewed recipient, decrypt into a restricted temporary directory, then run restore. No GPG adapter or key creation is implemented. No cloud bucket/account is created by the scripts.

## Recovery objectives and drill

Proposed municipal pilot targets (owner approval and measured infrastructure evidence pending): **RPO ≤15 minutes**, **RTO ≤60 minutes**. PostgreSQL scheduled logical dumps alone reach the RPO only when their interval and completion meet it; a managed continuous WAL/PITR service is recommended for larger deployments and is **documented-only**. File mode needs scheduled maintenance backups; choose an honest RPO matching their actual interval. The repository does not run a production backup scheduler, replicate off-site, retain cloud backups, or configure PITR.

Recovery procedure: isolate damaged storage, stop writers, choose a verified backup, provision an empty target, restore, check migrations/readiness and tenant audit integrity/counts, validate a synthetic intake/dispatch, then reopen traffic. Record actual backup age, drill duration, case/account/training/outbox counts, audit heads and operator sign-off. Do not describe the local synthetic drill as the production RTO.

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH TMPDIR=/workspace/civicops/tmp
dotnet build -c Release
./scripts/smoke_restore.sh
# Or file-only / inside an existing disposable wrapper:
./scripts/smoke_restore.sh file
./scripts/with_ephemeral_pg.sh ./scripts/smoke_restore.sh postgres
```

The drill creates synthetic cases, contacts, a scoped service account and DSAR; backs up, wipes the disposable storage, rejects checksum corruption, restores, restarts and compares HTTP records/counts and audit-chain results exactly. File mode additionally compares every durable artifact byte-for-byte and proves backup rejects the live application lock. PostgreSQL wipes only a wrapper-marked disposable cluster. No system database or provider is contacted. Cloud Object Lock, real disaster recovery timing, production encryption custody, rotation, retention, restore permissions and municipal recovery approval remain owner-pending.

The local encrypted drill also passed for both backends with [age 1.3.2](https://github.com/FiloSottile/age/releases/tag/v1.3.2), downloaded into `$TMPDIR/bin/age` and checked against the release's SHA-256. Keys were generated under the disposable drill directory and removed during cleanup. To repeat with existing age tools:

```bash
CIVICOPS_SMOKE_ENCRYPTION=age ./scripts/smoke_restore.sh
python3 -m unittest discover -s scripts -p test_backup_restore.py
```

The 15 Python safety tests check archive traversal/absolute paths, links, duplicate/alias entries, nonempty target preservation, checksum/manifest rejection, secret-file precedence and safe connection parsing. Encryption requires an explicit selection; the default drill exercises cleartext backups with restricted permissions. With the local disposable wrapper, the PostgreSQL drill also stops and restarts the database while CivicOps runs, proving that liveness survives, readiness reports 503, and readiness recovers after restart. This outage exercise uses only the wrapper-owned cluster; CI service-container recovery runs without stopping its database.
