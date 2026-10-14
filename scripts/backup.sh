#!/usr/bin/env bash
# Destination is a new directory. No connection credentials appear in arguments/output.
set -euo pipefail
exec python3 "$(dirname "$0")/backup_restore.py" backup "$@"
