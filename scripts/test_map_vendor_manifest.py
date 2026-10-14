"""Fast offline guards for manifests that may drive npm advisory queries."""

import base64
import copy
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.dont_write_bytecode = True
from map_vendor_manifest import MapVendorManifestError, load_map_vendors, npm_advisory_manifest, verify_map_vendor_lock


class MapVendorManifestTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="civicops-map-manifest-test-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / "vendor"
        self.root.mkdir()
        self.path = self.root / "map-vendors.json"
        packages = []
        for name, version, licence, licence_filename in (
            ("leaflet", "1.9.4", "BSD-2-Clause", "LICENSE"),
            ("leaflet.markercluster", "1.5.3", "MIT", "MIT-LICENCE.txt"),
        ):
            files = {}
            for filename in (f"{name}.js", licence_filename):
                relative = f"{name}-{version}/{filename}"
                target = self.root / relative
                target.parent.mkdir(exist_ok=True)
                content = ("Synthetic package provenance fixture " + relative).encode()
                target.write_bytes(content)
                files[relative] = hashlib.sha256(content).hexdigest()
            packages.append({"name": name, "version": version, "license": licence,
                "source": f"https://registry.npmjs.org/{name}/-/{name}-{version}.tgz",
                "sha512": base64.b64encode(b"0" * 64).decode(), "files": files})
        self.manifest = {"runtimeCdn": False, "packages": packages, "heatLayer": "Synthetic CivicOps canvas fixture"}
        self.write(self.manifest)

    def write(self, manifest):
        self.path.write_text(json.dumps(manifest))

    def reject(self, mutation):
        manifest = copy.deepcopy(self.manifest)
        mutation(manifest)
        self.write(manifest)
        with self.assertRaises(MapVendorManifestError):
            load_map_vendors(self.path)

    def test_valid_packages_produce_sorted_exact_dependencies_without_lifecycle_scripts(self):
        self.manifest["packages"].reverse()
        self.write(self.manifest)
        packages = load_map_vendors(self.path)
        self.assertEqual(["leaflet", "leaflet.markercluster"], [package["name"] for package in packages])
        npm = npm_advisory_manifest(packages)
        self.assertEqual({"leaflet": "1.9.4", "leaflet.markercluster": "1.5.3"}, npm["dependencies"])
        self.assertTrue(npm["private"])
        self.assertNotIn("scripts", npm)

    def test_ranges_tags_urls_unicode_and_noncanonical_versions_are_rejected(self):
        for version in ("^1.9.4", "~1.9.4", "latest", "file:../../package", "https://example.invalid/pkg.tgz",
                        "git+https://example.invalid/pkg", "1.09.4", "1.9.٤", "1.9.4\n", None):
            with self.subTest(version=version):
                self.reject(lambda m: m["packages"][0].update(version=version))

    def test_unknown_duplicate_missing_and_extra_packages_are_rejected(self):
        for name in ("unreviewed-package", "../leaflet", "@private/leaflet", "__proto__", None, "leaflet.markercluster"):
            with self.subTest(name=name):
                self.reject(lambda m: m["packages"][0].update(name=name))
        self.reject(lambda m: m["packages"].pop())
        self.reject(lambda m: m["packages"].append(copy.deepcopy(m["packages"][0])))

    def test_runtime_cdn_and_unknown_manifest_fields_are_rejected(self):
        for runtime in (True, "false", 0, None):
            with self.subTest(runtime=runtime):
                self.reject(lambda m: m.update(runtimeCdn=runtime))
        self.reject(lambda m: m.update(scripts={"install": "synthetic forbidden lifecycle hook"}))
        self.reject(lambda m: m["packages"][0].update(installScript="synthetic forbidden hook"))

    def test_unreviewed_sources_and_invalid_tarball_integrity_are_rejected(self):
        for source in ("http://registry.npmjs.org/leaflet/-/leaflet-1.9.4.tgz",
                       "https://example.invalid/leaflet/-/leaflet-1.9.4.tgz",
                       "https://user@registry.npmjs.org/leaflet/-/leaflet-1.9.4.tgz",
                       "https://registry.npmjs.org/leaflet/-/leaflet-1.9.4.tgz?token=synthetic",
                       "https://registry.npmjs.org/\nleaflet/-/leaflet-1.9.4.tgz",
                       "https://registry.npmjs.org/leaflet/-/leaflet-2.0.0.tgz", None):
            with self.subTest(source=source):
                self.reject(lambda m: m["packages"][0].update(source=source))
        for integrity in ("AAAA", "invalid!", None):
            with self.subTest(integrity=integrity):
                self.reject(lambda m: m["packages"][0].update(sha512=integrity))

    def test_unsafe_paths_changed_files_and_missing_licences_are_rejected(self):
        for filename in ("../outside.js", "/outside.js", "leaflet-1.9.4/../../outside.js", "leaflet-1.9.4\\outside.js"):
            with self.subTest(filename=filename):
                self.reject(lambda m: m["packages"][0]["files"].update({filename: "0" * 64}))
        self.reject(lambda m: m["packages"][0].update(license="unreviewed"))
        self.reject(lambda m: m["packages"][0]["files"].pop("leaflet-1.9.4/LICENSE"))
        (self.root / "leaflet-1.9.4/leaflet.js").write_bytes(b"Changed synthetic fixture")
        self.write(self.manifest)
        with self.assertRaises(MapVendorManifestError):
            load_map_vendors(self.path)

    def test_symlink_cannot_escape_vendor_directory_even_when_the_hash_matches(self):
        target = self.root / "leaflet-1.9.4/leaflet.js"
        outside = Path(self.directory.name) / "outside.js"
        outside.write_bytes(target.read_bytes())
        target.unlink()
        target.symlink_to(outside)
        with self.assertRaises(MapVendorManifestError):
            load_map_vendors(self.path)

    def test_duplicate_json_fields_are_rejected_even_when_the_last_value_is_valid(self):
        self.path.write_text(json.dumps(self.manifest).replace('"runtimeCdn": false', '"runtimeCdn": false, "runtimeCdn": false', 1))
        with self.assertRaises(MapVendorManifestError):
            load_map_vendors(self.path)

    def test_cli_writes_only_a_temporary_private_npm_manifest(self):
        output = Path(self.directory.name) / "package.json"
        helper = Path(__file__).with_name("map_vendor_manifest.py")
        result = subprocess.run([sys.executable, str(helper), str(self.path), str(output)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(npm_advisory_manifest(load_map_vendors(self.path)), json.loads(output.read_text()))
        forbidden = helper.parent.parent / "package.json"
        result = subprocess.run([sys.executable, str(helper), str(self.path), str(forbidden)], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("outside the repository", result.stderr)

    def registry_lock(self):
        packages = load_map_vendors(self.path)
        return {"lockfileVersion": 3, "packages": {
            "": {"dependencies": npm_advisory_manifest(packages)["dependencies"]},
            **{"node_modules/" + package["name"]: {"version": package["version"], "resolved": package["source"],
                "integrity": "sha512-" + package["sha512"]} for package in packages},
        }}

    def test_registry_lock_requires_matching_pinned_versions_and_archive_integrity(self):
        packages = load_map_vendors(self.path); path = Path(self.directory.name) / "package-lock.json"
        valid = self.registry_lock(); path.write_text(json.dumps(valid)); verify_map_vendor_lock(packages, path)
        for field, value in (("version", "2.0.0"), ("integrity", "sha512-" + base64.b64encode(b"1" * 64).decode())):
            with self.subTest(field=field):
                invalid = copy.deepcopy(valid); invalid["packages"]["node_modules/leaflet"][field] = value
                path.write_text(json.dumps(invalid))
                with self.assertRaises(MapVendorManifestError):
                    verify_map_vendor_lock(packages, path)
        invalid = copy.deepcopy(valid); invalid["packages"]["node_modules/unreviewed"] = {}
        path.write_text(json.dumps(invalid))
        with self.assertRaises(MapVendorManifestError):
            verify_map_vendor_lock(packages, path)

    def test_registry_lock_rejects_an_unreviewed_archive_host_even_with_matching_integrity(self):
        packages = load_map_vendors(self.path); lock = self.registry_lock()
        lock["packages"]["node_modules/leaflet"]["resolved"] = "https://example.invalid/leaflet/-/leaflet-1.9.4.tgz"
        path = Path(self.directory.name) / "package-lock.json"; path.write_text(json.dumps(lock))
        with self.assertRaises(MapVendorManifestError):
            verify_map_vendor_lock(packages, path)


if __name__ == "__main__":
    unittest.main()
