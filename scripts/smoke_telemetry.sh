#!/usr/bin/env bash
# Real OTLP exporters, private loopback collector, synthetic offline provider fallback.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR="${TMPDIR:-/tmp}"
exec python3 scripts/telemetry_smoke.py "${1:-file}"
