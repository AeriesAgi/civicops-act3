#!/usr/bin/env bash
# Verify checksum before restoring to an empty target. Application must be stopped.
set -euo pipefail
exec python3 "$(dirname "$0")/backup_restore.py" restore "$@"
