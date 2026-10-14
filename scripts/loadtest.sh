#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR="${TMPDIR:-/tmp}" DOTNET_PROCESSOR_COUNT=${DOTNET_PROCESSOR_COUNT:-2}
mkdir -p "$TMPDIR"
mode=${1:-all}
export CIVICOPS_LOADTEST_RESULTS=${CIVICOPS_LOADTEST_RESULTS:-$(mktemp -d "$TMPDIR/civicops-loadtest.XXXXXXXX")}
mkdir -p "$CIVICOPS_LOADTEST_RESULTS"
if [[ "$mode" == all ]]; then
  dotnet build -c Release > "$CIVICOPS_LOADTEST_RESULTS/build.log" 2>&1
  ./scripts/loadtest.sh file
  ./scripts/with_ephemeral_pg.sh ./scripts/loadtest.sh postgres
  echo "PASS: bounded file/PostgreSQL load tests. Results: $CIVICOPS_LOADTEST_RESULTS"
  exit
fi
[[ "$mode" == file || "$mode" == postgres ]] || { echo 'Usage: scripts/loadtest.sh [all|file|postgres]' >&2; exit 2; }
command -v k6 > /dev/null || { echo 'Install the pinned Grafana k6 release documented in LOAD_TEST_RESULTS.md.' >&2; exit 2; }
export SMOKE_STORAGE="$mode" SMOKE_REQUEST_LIMIT=10000
source scripts/smoke_common.sh
export CIVICOPS_LOADTEST_BASE_URL="$SMOKE_BASE_URL"
export K6_BROWSER_EXECUTABLE_PATH=${CIVICOPS_CHROME_PATH:-/usr/bin/google-chrome}
[[ -x "$K6_BROWSER_EXECUTABLE_PATH" ]] || { echo 'The SSE test requires Chrome/Chromium.' >&2; exit 2; }
export K6_BROWSER_ARGS='disable-background-networking,disable-component-update,disable-sync'
mkdir -p "$CIVICOPS_LOADTEST_RESULTS/$mode"
cleanup_loadtest() {
  cp "$smoke_dir/server.log" "$CIVICOPS_LOADTEST_RESULTS/$mode/server.log" 2>/dev/null || true
  cleanup_smoke
}
trap cleanup_loadtest EXIT
curl --fail --silent -X POST "$SMOKE_BASE_URL/api/demo/seed" -H "Authorization: Bearer $CIVICOPS_DISPATCH_TOKEN" > "$CIVICOPS_LOADTEST_RESULTS/$mode/seed.json"
# Warm the seeded state and let its delivery/reconciliation settle before timing new work.
# New reports during measurements still use the real hosted outbox worker.
for attempt in $(seq 1 100); do
  curl --fail --silent "$SMOKE_BASE_URL/api/integrations/outbox" -H "Authorization: Bearer $CIVICOPS_DISPATCH_TOKEN" > "$CIVICOPS_LOADTEST_RESULTS/$mode/warmup.json"
  if python3 - "$CIVICOPS_LOADTEST_RESULTS/$mode/warmup.json" <<'PY'
import json, sys
from pathlib import Path
status = json.loads(Path(sys.argv[1]).read_text())['status']
raise SystemExit(0 if status['pending'] == 0 and status['deadLetter'] == 0 else 1)
PY
  then break; fi
  sleep .1
done
python3 - "$CIVICOPS_LOADTEST_RESULTS/$mode/warmup.json" <<'PY'
import json, sys
from pathlib import Path
status = json.loads(Path(sys.argv[1]).read_text())['status']
assert status['pending'] == 0 and status['deadLetter'] == 0, 'Synthetic seed did not settle before measurement.'
PY
python3 - "$CIVICOPS_LOADTEST_RESULTS/$mode/machine.json" <<'PY'
import json, os, platform, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path
Path(sys.argv[1]).write_text(json.dumps({
    'utc': datetime.now(timezone.utc).isoformat(), 'platform': platform.platform(),
    'logical_cpus': os.cpu_count(), 'dotnet_processor_count': os.environ.get('DOTNET_PROCESSOR_COUNT'),
    'cpu': subprocess.check_output(['lscpu'], text=True),
    'memory': subprocess.check_output(['free', '-h'], text=True),
    'k6': subprocess.check_output(['k6', 'version'], text=True).strip(),
    'dotnet': subprocess.check_output(['dotnet', '--version'], text=True).strip(),
    'caveat': 'Shared VM, loopback, synthetic demo, no GPU or live providers. Not production capacity.',
}, indent=2))
PY
for scenario in resident_intake dispatcher_queue training_api sse_connections; do
  export CIVICOPS_LOADTEST_SUMMARY="$CIVICOPS_LOADTEST_RESULTS/$mode/$scenario.json"
  # No remote module imports/cloud output; native HTTP and Chromium are local only.
  k6 run --quiet "loadtest/k6/$scenario.js" > "$CIVICOPS_LOADTEST_RESULTS/$mode/$scenario.log" 2>&1 || {
    cat "$CIVICOPS_LOADTEST_RESULTS/$mode/$scenario.log"; exit 1;
  }
  echo "PASS: $mode $scenario thresholds"
done
curl --fail --silent "$SMOKE_BASE_URL/api/audit/verify" -H "Authorization: Bearer $CIVICOPS_DISPATCH_TOKEN" > "$CIVICOPS_LOADTEST_RESULTS/$mode/audit.json"
python3 - "$CIVICOPS_LOADTEST_RESULTS/$mode" <<'PY'
import json, sys
from pathlib import Path
root = Path(sys.argv[1])
audit = json.loads((root / 'audit.json').read_text())
assert audit['valid'], audit
for path in root.glob('*_*.json'):
    metrics = json.loads(path.read_text())['metrics']
    assert all(value['ok'] for metric in metrics.values() for value in metric.get('thresholds', {}).values()), path
print('PASS: audit chain and saved threshold outcomes')
PY
cp "$smoke_dir/server.log" "$CIVICOPS_LOADTEST_RESULTS/$mode/server.log"
echo "Results: $CIVICOPS_LOADTEST_RESULTS/$mode"
