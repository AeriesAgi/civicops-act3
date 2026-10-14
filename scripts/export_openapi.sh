#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/smoke_common.sh"
output=${1:-"$repo_root/docs/api/openapi.json"}
mkdir -p "$(dirname "$output")"
curl --fail --silent --show-error "$SMOKE_BASE_URL/openapi.json" > "$smoke_dir/openapi.json"
python3 - "$smoke_dir/openapi.json" "$output" <<'PY'
import json
import sys
from pathlib import Path
value = json.loads(Path(sys.argv[1]).read_text())
Path(sys.argv[2]).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')
print(f"Exported {len(value['paths'])} paths / {sum(len(p) for p in value['paths'].values())} operations to {sys.argv[2]}")
PY
