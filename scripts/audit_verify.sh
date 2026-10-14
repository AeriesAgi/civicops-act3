#!/usr/bin/env bash
# Offline artifact verification: no running CivicOps server or database is needed.
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [[ $# -ne 1 || $1 != /* ]]; then
  printf 'Usage: %s <absolute-directory-containing-audit.jsonl-and-manifest.json>\n' "$0" >&2
  exit 2
fi
audit_dll="$repo_root/products/civicops/bin/Release/net10.0/CivicOps.dll"
if [[ ! -f "$audit_dll" ]]; then dotnet build "$repo_root/products/civicops/CivicOps.csproj" -c Release; fi
exec dotnet "$audit_dll" audit verify "$1"
