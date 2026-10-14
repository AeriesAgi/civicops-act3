#!/usr/bin/env bash
set -euo pipefail
base_url=${CIVICOPS_BASE_URL:-http://127.0.0.1:5080}
case "$base_url" in http://127.0.0.1:*|http://localhost:*) ;; *) printf '%s\n' 'Seed script only targets localhost. Use dispatcher UI for a deliberate remote demo seed.' >&2; exit 1 ;; esac
curl --fail --silent --show-error -X POST "$base_url/api/demo/seed"
printf '\n'
