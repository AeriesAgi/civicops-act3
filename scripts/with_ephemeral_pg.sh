#!/usr/bin/env bash
set -euo pipefail
# PostgreSQL 17 only, own-user disposable cluster, loopback and a private Unix socket.
export TMPDIR="${TMPDIR:-/tmp}"
pg_bin=${CIVICOPS_PG_BIN:-/usr/lib/postgresql/17/bin}
[[ $# -gt 0 ]] || { echo 'Usage: scripts/with_ephemeral_pg.sh COMMAND [ARG ...]' >&2; exit 2; }
[[ $(id -u) -ne 0 ]] || { echo 'Run the disposable cluster as an unprivileged user.' >&2; exit 2; }
mkdir -p "$TMPDIR"
cluster_dir=$(mktemp -d "$TMPDIR/civicops-pg.XXXXXXXX")
cleanup() {
  "$pg_bin/pg_ctl" -D "$cluster_dir/data" -m immediate -w stop > /dev/null 2>&1 || true
  rm -rf "$cluster_dir"
}
trap cleanup EXIT INT TERM
mkdir -m 700 "$cluster_dir/socket"
port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
"$pg_bin/initdb" -D "$cluster_dir/data" --auth-local=trust --auth-host=trust --no-locale --encoding=UTF8 > "$cluster_dir/init.log"
"$pg_bin/pg_ctl" -D "$cluster_dir/data" -l "$cluster_dir/server.log" -o "-p $port -h 127.0.0.1 -k $cluster_dir/socket -c max_connections=30 -c shared_buffers=32MB" -w start > /dev/null
"$pg_bin/createdb" -h 127.0.0.1 -p "$port" civicops_test
export CIVICOPS_EPHEMERAL_PG=1
export CIVICOPS_EPHEMERAL_PG_DIR="$cluster_dir"
export CIVICOPS_TEST_DATABASE_URL="Host=127.0.0.1;Port=$port;Database=civicops_test;Username=$(id -un);SSL Mode=Disable;Maximum Pool Size=5"
export CIVICOPS_DATABASE_URL="$CIVICOPS_TEST_DATABASE_URL"
export PGHOST=127.0.0.1 PGPORT="$port" PGDATABASE=civicops_test PGUSER="$(id -un)"
"$@"
