#!/usr/bin/env bash
set -euo pipefail
export SMOKE_DEMO_MODE=true
source "$(dirname "$0")/smoke_common.sh"
export CIVICOPS_BROWSER_BASE_URL="$SMOKE_BASE_URL"
# Leave docs/media untouched unless CIVICOPS_UPDATE_MEDIA=1 is explicitly requested.
cd "$repo_root"
node "$repo_root/scripts/browser_smoke.mjs"
