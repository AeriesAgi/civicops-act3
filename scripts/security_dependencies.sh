#!/usr/bin/env bash
# Fail on advisory findings: dotnet list itself exits zero for vulnerable packages.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
report=$(mktemp "${TMPDIR:-/tmp}/civicops-vulnerabilities.XXXXXX.json")
map_audit_directory=""
cleanup_dependencies() {
  rm -f "$report"
  if [[ -n "$map_audit_directory" ]]; then rm -rf -- "$map_audit_directory"; fi
}
trap cleanup_dependencies EXIT
dotnet list CivicOps.sln package --vulnerable --include-transitive --format json --output-version 1 > "$report"
python3 - "$report" <<'PY'
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text())
if report.get("version") != 1 or not report.get("projects") or report.get("errors"):
    raise SystemExit("Dependency advisory query did not produce a complete report.")
findings = []
for project in report["projects"]:
    for framework in project.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(kind, []):
                for advisory in package.get("vulnerabilities", []):
                    findings.append((package["id"], package["resolvedVersion"], advisory["severity"], advisory["advisoryurl"]))
if findings:
    for package, version, severity, url in findings:
        print(f"Vulnerable dependency: {package}@{version} ({severity}): {url}", file=sys.stderr)
    raise SystemExit(1)
print(f"PASS: NuGet advisory scan; {len(report['projects'])} projects; 0 vulnerable direct/transitive packages.")
PY

manifests=0
while IFS= read -r -d '' manifest; do
  manifests=$((manifests + 1))
  directory=$(dirname "$manifest")
  if [[ ! -f "$directory/package-lock.json" && ! -f "$directory/npm-shrinkwrap.json" ]]; then
    echo "A pinned npm lockfile is required for $manifest." >&2
    exit 1
  fi
  npm ci --prefix "$directory" --ignore-scripts --no-audit --no-fund
  npm audit --prefix "$directory" --audit-level=low
done < <(git ls-files -z -- 'package.json' '**/package.json')
echo "PASS: npm advisory gate; $manifests repository package.json manifests audited."

# Vendored browser packages have no repository npm project. Validate the exact
# reviewed names/versions and local provenance before creating a temporary lock.
python3 scripts/test_map_vendor_manifest.py
map_audit_directory=$(mktemp -d "${TMPDIR:-/tmp}/civicops-map-advisories.XXXXXXXX")
python3 scripts/map_vendor_manifest.py products/civicops/wwwroot/vendor/map-vendors.json "$map_audit_directory/package.json"
npm install --prefix "$map_audit_directory" --package-lock-only --ignore-scripts --no-audit --no-fund \
  --registry https://registry.npmjs.org --cache "$map_audit_directory/cache"
python3 scripts/map_vendor_manifest.py products/civicops/wwwroot/vendor/map-vendors.json --verify-lock "$map_audit_directory/package-lock.json"
npm audit --prefix "$map_audit_directory" --audit-level=low \
  --registry https://registry.npmjs.org --cache "$map_audit_directory/cache"
echo "PASS: vendored map npm advisory gate; 2 reviewed pinned packages; official registry; no lifecycle scripts."
