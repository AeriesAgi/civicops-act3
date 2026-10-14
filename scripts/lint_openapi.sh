#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
export TMPDIR="${TMPDIR:-/tmp}"
export CIVICOPS_SPECTRAL_TOOLS_DIR=${CIVICOPS_SPECTRAL_TOOLS_DIR:-"$TMPDIR/openapi-lint-tools"}
if [[ ${1:-} == --install ]]; then
  shift
  mkdir -p "$CIVICOPS_SPECTRAL_TOOLS_DIR"
  # The CLI currently brings an unfixable braces advisory via file globbing; the official
  # Spectral API provides identical OAS rules without that CLI-only dependency chain.
  npm install --prefix "$CIVICOPS_SPECTRAL_TOOLS_DIR" --no-audit --no-fund --ignore-scripts --save-exact \
    @stoplight/spectral-core@1.24.0 @stoplight/spectral-rulesets@1.23.0 lodash@4.18.1
fi
if [[ ! -d "$CIVICOPS_SPECTRAL_TOOLS_DIR/node_modules/@stoplight/spectral-core" ]]; then
  echo 'Spectral tools absent: run ./scripts/lint_openapi.sh --install (tools stay under TMPDIR).' >&2
  exit 1
fi
npm audit --prefix "$CIVICOPS_SPECTRAL_TOOLS_DIR" --audit-level=low
node "$repo_root/scripts/lint_openapi.mjs" "${1:-"$repo_root/docs/api/openapi.json"}"
