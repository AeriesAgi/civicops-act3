#!/usr/bin/env bash
# Official Apache-2.0 CycloneDX .NET CLI, pinned; no provider/account calls.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
temporary_root="${TMPDIR:-/tmp}"
tool_directory="$temporary_root/civicops-sbom-tools"
output="${1:-docs/security/sbom.cdx.json}"
mkdir -p "$tool_directory" "$(dirname "$output")"
if [[ ! -x "$tool_directory/dotnet-CycloneDX" ]] \
  || ! dotnet tool list --tool-path "$tool_directory" | awk '$1 == "cyclonedx" && $2 == "6.2.0" { found = 1 } END { exit !found }'; then
  dotnet tool install CycloneDX --version 6.2.0 --tool-path "$tool_directory"
fi
# Dotnet is installed under $HOME/.dotnet on the local verification box.
if [[ -z "${DOTNET_ROOT:-}" ]]; then
  export DOTNET_ROOT
  DOTNET_ROOT=$(dirname "$(readlink -f "$(command -v dotnet)")")
fi
dotnet restore CivicOps.sln --verbosity quiet
"$tool_directory/dotnet-CycloneDX" CivicOps.sln --configuration Release \
  --disable-package-restore --output-format Json --spec-version 1.6 --no-serial-number \
  --set-name CivicOps --set-version 0.0.0 --output "$(dirname "$output")" --filename "$(basename "$output")"
python3 - "$output" <<'PY'
import json
import re
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path("scripts").resolve()))
from map_vendor_manifest import load_map_vendors

path = Path(sys.argv[1])
bom = json.loads(path.read_text())
if bom.get("bomFormat") != "CycloneDX" or bom.get("specVersion") != "1.6":
    raise SystemExit("Unexpected CycloneDX output.")
# Include the optional pinned browser bundles: they are CDN-loaded, not NuGet dependencies.
source = Path("products/civicops/wwwroot/app.js").read_text()
import base64
root_ref = bom["metadata"]["component"]["bom-ref"]
root_dependency = next(item for item in bom["dependencies"] if item["ref"] == root_ref)
for package, pattern in (
    ("@elevenlabs/client", r'https://cdn\.jsdelivr\.net/npm/@elevenlabs/client@([\d.]+)/dist/lib\.iife\.js'),
    ("@elevenlabs/convai-widget-embed", r'https://unpkg\.com/@elevenlabs/convai-widget-embed@([\d.]+)/dist/index\.js'),
):
    match = re.search(pattern, source)
    integrity = re.search(r'sha384-([A-Za-z0-9+/=]+)', source[match.end():match.end()+500]) if match else None
    if not match or not integrity:
        raise SystemExit(f"Pinned URL/SRI could not be inventoried: {package}")
    version = match.group(1)
    ref = f"pkg:npm/{package.replace('@', '%40')}@{version}"
    bom.setdefault("components", []).append({
        "type": "library", "bom-ref": ref, "name": package, "version": version,
        "purl": ref, "scope": "optional", "licenses": [{"license": {"id": "MIT"}}],
        "hashes": [{"alg": "SHA-384", "content": base64.b64decode(integrity.group(1)).hex()}],
        "externalReferences": [{"type": "distribution", "url": match.group(0)}],
        "properties": [{"name": "civicops:scope", "value": "optional configured browser bundle; embedded dependency graph requires separate deployment review"}]
    })
    root_dependency["dependsOn"].append(ref)
    bom["dependencies"].append({"ref": ref, "dependsOn": []})
# The SDK also loads this separately pinned worklet. Hash is the npm tarball
# integrity, not a claim that the browser enforces SRI on AudioWorklet imports.
header_source = Path("products/civicops/Services/SecurityHeaders.cs").read_text()
worklet_url = "https://cdn.jsdelivr.net/npm/@alexanderolsen/libsamplerate-js@2.1.2/dist/libsamplerate.worklet.js"
if worklet_url not in header_source:
    raise SystemExit("Pinned audio worklet changed; review its inventory/hash before regenerating.")
worklet_ref = "pkg:npm/%40alexanderolsen/libsamplerate-js@2.1.2"
bom["components"].append({
    "type": "library", "bom-ref": worklet_ref, "name": "@alexanderolsen/libsamplerate-js", "version": "2.1.2",
    "purl": worklet_ref, "scope": "optional", "licenses": [{"license": {"id": "MIT"}}],
    "hashes": [{"alg": "SHA-512", "content": base64.b64decode("pIXQDX/DZIgz6pKInUddDd6Tnq/s2E9g4ZITkKX60kxM/nAuZcxYa/z0y/jwJbjyp/oKe+8/qHLSqMzQ18xSiQ==").hex()}],
    "externalReferences": [{"type": "distribution", "url": worklet_url}],
    "properties": [{"name": "civicops:scope", "value": "optional pinned audio worklet; hash inventories npm tarball; browser import has no independent SRI enforcement"}]
})
next(item for item in bom["dependencies"] if item["ref"].startswith("pkg:npm/%40elevenlabs/client@"))["dependsOn"].append(worklet_ref)
bom["dependencies"].append({"ref": worklet_ref, "dependsOn": []})
# Required map libraries are vendored locally, never fetched from a runtime CDN.
# Package hashes identify upstream npm tarballs; per-file SHA-256 properties
# identify the exact browser assets and licence files shipped in this repository.
vendor_root = Path("products/civicops/wwwroot/vendor").resolve()
map_refs = {}
for package in load_map_vendors(vendor_root / "map-vendors.json"):
    name, version = package["name"], package["version"]
    tarball_hash = base64.b64decode(package["sha512"], validate=True)
    properties = [
        {"name": "civicops:scope", "value": "required vendored browser library; SHA-512 inventories upstream npm tarball; per-file SHA-256 values verify shipped assets and licence files"},
        {"name": "civicops:source-manifest", "value": "products/civicops/wwwroot/vendor/map-vendors.json"},
    ]
    for filename, expected_hash in sorted(package["files"].items()):
        properties.append({"name": f"civicops:vendored-sha256:{filename}", "value": expected_hash})
    ref = f"pkg:npm/{name}@{version}"
    if ref in map_refs.values() or any(item["bom-ref"] == ref for item in bom["components"]):
        raise SystemExit(f"Duplicate map component: {ref}")
    map_refs[name] = ref
    bom["components"].append({
        "type": "library", "bom-ref": ref, "name": name, "version": version,
        "purl": ref, "scope": "required", "licenses": [{"license": {"id": package["license"]}}],
        "hashes": [{"alg": "SHA-512", "content": tarball_hash.hex()}],
        "externalReferences": [{"type": "distribution", "url": package["source"]}], "properties": properties,
    })
    root_dependency["dependsOn"].append(ref)
    bom["dependencies"].append({"ref": ref, "dependsOn": []})
if "leaflet.markercluster" in map_refs:
    if "leaflet" not in map_refs:
        raise SystemExit("Leaflet.markercluster requires inventoried Leaflet.")
    next(item for item in bom["dependencies"] if item["ref"] == map_refs["leaflet.markercluster"])["dependsOn"].append(map_refs["leaflet"])
# No absolute build-box paths; keep the committed inventory reproducible.
bom.get("metadata", {}).pop("timestamp", None)
bom["metadata"].setdefault("properties", []).append({
    "name": "civicops:scope",
    "value": "NuGet direct/transitive application and test packages plus required vendored map libraries and pinned optional SDK; excludes .NET shared runtime, OS/container, CLI tools, CDN transitive module graph and model weights"
})
bom["components"].sort(key=lambda item: item.get("bom-ref", item["name"]))
path.write_text(json.dumps(bom, indent=2) + "\n")
print(f"PASS: CycloneDX 1.6 SBOM; {len(bom['components'])} components; {path.stat().st_size} bytes.")
PY
