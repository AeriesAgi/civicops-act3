"""Validate reviewed local map packages before inventory or registry advisory reads."""

import argparse
import base64
import binascii
import hashlib
import json
import re
from pathlib import Path, PurePosixPath


class MapVendorManifestError(ValueError):
    pass


REVIEWED_PACKAGES = {
    "leaflet": ("BSD-2-Clause", "LICENSE"),
    "leaflet.markercluster": ("MIT", "MIT-LICENCE.txt"),
}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise MapVendorManifestError(f"Duplicate map manifest field: {key}")
        result[key] = value
    return result


def load_map_vendors(manifest_path):
    """Return sorted pinned packages after validating their local provenance."""
    manifest_path = Path(manifest_path)
    vendor_root = manifest_path.parent.resolve()
    manifest = json.loads(manifest_path.read_text(), object_pairs_hook=unique_object)
    if not isinstance(manifest, dict) or set(manifest) != {"runtimeCdn", "packages", "heatLayer"} \
            or manifest["runtimeCdn"] is not False or not isinstance(manifest["heatLayer"], str):
        raise MapVendorManifestError("Map manifest must declare local packages, runtimeCdn=false and the heat-layer scope.")
    packages = manifest["packages"]
    if not isinstance(packages, list) or len(packages) != len(REVIEWED_PACKAGES):
        raise MapVendorManifestError("Map manifest must inventory exactly the reviewed Leaflet and markercluster packages.")
    names = set()
    for package in packages:
        if not isinstance(package, dict) or set(package) != {"name", "version", "license", "source", "sha512", "files"}:
            raise MapVendorManifestError("Map package must provide exactly its reviewed provenance fields.")
        name, version = package["name"], package["version"]
        if not isinstance(name, str) or name not in REVIEWED_PACKAGES or name in names:
            raise MapVendorManifestError("Unknown or duplicate map package name.")
        if not isinstance(version, str) or re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", version) is None:
            raise MapVendorManifestError("Map package version must be an exact stable npm version, without ranges, URLs or tags.")
        names.add(name)
        if package["source"] != f"https://registry.npmjs.org/{name}/-/{name}-{version}.tgz":
            raise MapVendorManifestError(f"Map package must use its pinned official npm tarball: {name}")
        try:
            tarball_hash = base64.b64decode(package["sha512"], validate=True)
        except (ValueError, TypeError, binascii.Error) as error:
            raise MapVendorManifestError(f"Invalid map tarball integrity: {name}") from error
        if len(tarball_hash) != 64 or base64.b64encode(tarball_hash).decode() != package["sha512"]:
            raise MapVendorManifestError(f"Map package requires canonical SHA-512 tarball integrity: {name}")
        licence, licence_filename = REVIEWED_PACKAGES[name]
        files = package["files"]
        prefix = f"{name}-{version}/"
        if package["license"] != licence or not isinstance(files, dict) or not files or prefix + licence_filename not in files:
            raise MapVendorManifestError(f"Map package requires its reviewed licence and shipped licence file: {name}")
        for filename, expected_hash in files.items():
            if not isinstance(filename, str) or not filename.startswith(prefix) or "\\" in filename \
                    or PurePosixPath(filename).is_absolute() or ".." in PurePosixPath(filename).parts \
                    or not isinstance(expected_hash, str) or re.fullmatch(r"[0-9a-f]{64}", expected_hash) is None:
                raise MapVendorManifestError(f"Invalid map vendor file provenance: {filename}")
            target = (vendor_root / filename).resolve()
            if not target.is_relative_to(vendor_root) or not target.is_file():
                raise MapVendorManifestError(f"Map vendor file must remain inside the vendor directory: {filename}")
            if hashlib.sha256(target.read_bytes()).hexdigest() != expected_hash:
                raise MapVendorManifestError(f"Map vendor file differs from reviewed manifest: {filename}")
    if names != set(REVIEWED_PACKAGES):
        raise MapVendorManifestError("Map manifest omitted a reviewed package.")
    return sorted(packages, key=lambda package: package["name"])


def npm_advisory_manifest(packages):
    return {
        "name": "civicops-vendored-map-advisory",
        "version": "0.0.0",
        "private": True,
        "dependencies": {package["name"]: package["version"] for package in packages},
    }


def verify_map_vendor_lock(packages, lock_path):
    """Match registry resolution to the reviewed archive before auditing it."""
    lock = json.loads(Path(lock_path).read_text(), object_pairs_hook=unique_object)
    if not isinstance(lock, dict) or lock.get("lockfileVersion") != 3 or not isinstance(lock.get("packages"), dict):
        raise MapVendorManifestError("Map advisory resolution requires a complete npm lockfile version 3.")
    entries = lock["packages"]
    expected = {""} | {"node_modules/" + package["name"] for package in packages}
    if set(entries) != expected or not isinstance(entries[""], dict) \
            or entries[""].get("dependencies") != npm_advisory_manifest(packages)["dependencies"]:
        raise MapVendorManifestError("Map advisory lock must resolve exactly the two reviewed pinned dependencies.")
    for package in packages:
        resolved = entries["node_modules/" + package["name"]]
        if not isinstance(resolved, dict) or resolved.get("version") != package["version"] \
                or resolved.get("resolved") != package["source"] \
                or resolved.get("integrity") != "sha512-" + package["sha512"]:
            raise MapVendorManifestError(f"Registry resolution differs from the reviewed map archive/version/integrity: {package['name']}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("output", type=Path, nargs="?")
    parser.add_argument("--verify-lock", type=Path)
    args = parser.parse_args()
    if bool(args.output) == bool(args.verify_lock):
        parser.error("Choose one temporary manifest output or --verify-lock.")
    if args.output and args.output.resolve().is_relative_to(Path(__file__).resolve().parent.parent):
        raise SystemExit("The advisory npm manifest must be generated outside the repository.")
    packages = load_map_vendors(args.manifest)
    if args.verify_lock:
        verify_map_vendor_lock(packages, args.verify_lock)
        print(f"PASS: map registry resolution; {len(packages)} exact pinned versions, official archive URLs and SHA-512 integrity values match the reviewed manifest.")
    else:
        args.output.write_text(json.dumps(npm_advisory_manifest(packages), sort_keys=True, indent=2) + "\n")
        print(f"PASS: reviewed map manifest; {len(packages)} exact pinned packages; shipped-file provenance verified.")
