#!/usr/bin/env bash
set -euo pipefail
export SMOKE_DEMO_MODE=true CIVICOPS_MAP_TILE_URL=none
source "$(dirname "$0")/smoke_common.sh"
export CIVICOPS_BROWSER_BASE_URL="$SMOKE_BASE_URL"
cd "$repo_root"
node "$repo_root/scripts/agency_browser_smoke.mjs"
python3 "$repo_root/scripts/agency_smoke.py"
