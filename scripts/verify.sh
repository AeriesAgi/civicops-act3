#!/usr/bin/env bash
# Repository-owned verification; the external round harness may invoke this too.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR="${TMPDIR:-/tmp}"
export CIVICOPS_PLAYWRIGHT_MODULE="${CIVICOPS_PLAYWRIGHT_MODULE:-$TMPDIR/browser-tools/node_modules/playwright-core/index.mjs}"
dotnet build -c Release --no-incremental
dotnet test -c Release --no-build
python3 scripts/check_docs.py
python3 -m unittest discover -s scripts -p test_check_docs.py
./scripts/validate_deployment.sh
./scripts/lint_openapi.sh --install
./scripts/smoke_lifecycle.sh
./scripts/smoke_e2e.sh
./scripts/smoke_browser.sh
./scripts/smoke_sos.sh
./scripts/smoke_agencies.sh
./scripts/smoke_security_privacy.sh
./scripts/smoke_widget_csp.sh
./scripts/smoke_restore.sh
./scripts/smoke_audit_export.sh
./scripts/smoke_telemetry.sh
python3 -m unittest discover -s scripts -p test_backup_restore.py
./scripts/with_ephemeral_pg.sh dotnet test -c Release --no-build
./scripts/with_ephemeral_pg.sh ./scripts/smoke_telemetry.sh postgres
./scripts/loadtest.sh
./scripts/security_dependencies.sh
gitleaks detect --source . --log-opts="--all" --redact
gitleaks dir . --redact
git diff --check
if [[ -n "$(git status --porcelain)" ]]; then
  git status --short
  echo 'Verification requires a clean committed worktree.' >&2
  exit 1
fi
echo 'PASS: all repository verification gates; worktree clean.'
