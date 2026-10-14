#!/usr/bin/env bash
# Both backends; caller can select file/postgres. All records and credentials are synthetic.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR="${TMPDIR:-/tmp}"
mode=${1:-both}
case "$mode" in
  both)
    "$0" file
    ./scripts/with_ephemeral_pg.sh "$0" postgres
    ;;
  file|postgres)
    python3 scripts/restore_smoke.py "$mode"
    ;;
  *) echo 'Usage: smoke_restore.sh [both|file|postgres]' >&2; exit 2;;
esac
