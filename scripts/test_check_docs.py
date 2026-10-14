import tempfile
import unittest
from pathlib import Path

from check_docs import anchors, check_links, inventory, link_details, links


class DocumentationLinksTests(unittest.TestCase):
    def test_markdown_html_and_reference_destinations(self):
        self.assertEqual(links('[a](<file with space.md> "title") ![b](image.png)\n[ref]: other.md#anchor\n<a href="part.md">Part</a>'),
                         ['file with space.md', 'image.png', 'other.md#anchor', 'part.md'])

    def test_balanced_and_escaped_parentheses(self):
        self.assertEqual(links(r'[a](path(a).md) [b](path\(b\).md)'), ['path(a).md', 'path(b).md'])

    def test_code_examples_are_ignored(self):
        self.assertEqual(links('```md\n[a](missing)\n```\n`[b](missing)`\n\n    [c](missing)\n[d](exists)'), ['exists'])

    def test_nested_list_links_and_continuations_are_checked(self):
        self.assertEqual(links('- Parent\n    - [Child](child.md)\n      Continued [guide](guide.md)\n\n      More [detail](detail.md)\n- [Last](last.md)'),
                         ['child.md', 'guide.md', 'detail.md', 'last.md'])

    def test_code_blocks_within_lists_are_ignored(self):
        self.assertEqual(links('- Parent\n\n      [code](missing.md)\n\n  [prose](valid.md)\n  ```md\n  [fenced](missing.md)\n  ```'), ['valid.md'])

    def test_indented_paragraph_continuation_is_not_code(self):
        self.assertEqual(links('Paragraph\n    [continued](valid.md)'), ['valid.md'])

    def test_balanced_and_escaped_label_brackets(self):
        self.assertEqual(links(r'[See [municipal] guide](guide.md) ![Image [detail]](image.png) [Escaped \] label](other.md) \[literal](ignored.md)'),
                         ['guide.md', 'image.png', 'other.md'])

    def test_angle_destinations_and_quoted_titles(self):
        self.assertEqual(links('[a](<file(.md>) [b](<file).md>) [c](normal.md "title with ( unmatched") [d](last.md \'title )\')'),
                         ['file(.md', 'file).md', 'normal.md', 'last.md'])

    def test_full_collapsed_and_shortcut_references_resolve(self):
        self.assertEqual(link_details('[Full][ GUIDE ] [Guide][] ![Image][guide] [guide]\n\n[guide]: valid.md#heading'),
                         (['valid.md#heading'] * 5, []))

    def test_undefined_full_and_collapsed_references_fail_the_gate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            document = root / 'README.md'
            document.write_text('[Full][missing] [Collapsed][] [ordinary prose]\n\n```md\n[Example][ignored]\n```')
            errors, count, external = check_links(root, [document])
            self.assertEqual(errors, ['README.md: undefined reference [missing]', 'README.md: undefined reference [collapsed]'])
            self.assertEqual((count, external), (0, 0))

    def test_nested_and_angle_links_fail_for_missing_real_targets(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            document = root / 'README.md'
            document.write_text('- List\n    - [Nested [label]](<missing(.md>)')
            errors, count, external = check_links(root, [document])
            self.assertEqual(errors, ['README.md -> missing(.md: missing target'])
            self.assertEqual((count, external), (1, 0))

    def test_duplicate_unicode_headings_and_explicit_anchors(self):
        self.assertEqual(anchors('# Start here!\n# Start here!\n## São Paulo\n<a id="manual"></a>'),
                         {'start-here', 'start-here-1', 'são-paulo', 'manual'})

    def test_missing_files_fragments_and_escape_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            document = root / 'README.md'
            document.write_text('[a](absent.md) [b](README.md#absent) [c](../outside.md)')
            errors, count, external = check_links(root, [document])
            self.assertEqual((len(errors), count, external), (3, 3, 0))

    def test_encoded_paths_heading_and_external_urls(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            target = root / 'file name.md'
            target.write_text('# Valid heading\n')
            document = root / 'README.md'
            document.write_text('[a](file%20name.md#valid-heading) [b](https://example.invalid/)')
            self.assertEqual(check_links(root, [document]), ([], 1, 1))


class EnvironmentInventoryTests(unittest.TestCase):
    def create_fixture(self, root, names, identity='', secret=''):
        services = root / 'products/civicops/Services'
        services.mkdir(parents=True)
        (services / 'IdentityOptions.cs').write_text(identity)
        (services / 'SecretProvider.cs').write_text(secret)
        content = ''.join('# Comment for ' + name + '\n' + name + '=\n' for name in names)
        (root / '.env.example').write_text(content)
        (root / 'products/civicops/.env.example').write_text(content)
        return services

    def test_runtime_and_deployment_variables_must_be_in_the_inventory(self):
        names = ['ASPNETCORE_HTTP_PORTS', 'PUBLIC_BASE_URL', 'AllowedHosts', 'POSTGRES_INITDB_ARGS']
        for missing in names:
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary).resolve()
                services = self.create_fixture(root, [name for name in names if name != missing])
                (services / 'Configuration.cs').write_text(';'.join('config["' + name + '"]' for name in names))
                errors, count = inventory(root)
                self.assertEqual(errors, ['Missing environment placeholder: ' + missing])
                self.assertEqual(count, 3)

    def test_dynamic_claim_and_read_settings_cannot_drift(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            self.create_fixture(root, [], identity='Read("CLIENT_ID"); Claim("TENANT_CLAIM", "municipality_id");')
            errors, count = inventory(root)
            self.assertEqual(errors, ['Missing dynamic OIDC setting: CLIENT_ID', 'Missing dynamic OIDC setting: TENANT_CLAIM'])
            self.assertEqual(count, 0)

    def test_mounted_secret_placeholder_is_required(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            self.create_fixture(root, ['ELEVENLABS_API_KEY'], secret='Names = ["ELEVENLABS_API_KEY"]; public static ISecretProvider Create() {}')
            self.assertEqual(inventory(root), (['Missing mounted secret setting: ELEVENLABS_API_KEY_FILE'], 1))

    def test_prohibited_trust_mode_guard_is_not_supported_configuration(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            services = self.create_fixture(root, [])
            (services / 'Guard.cs').write_text('Reject("POSTGRES_HOST_AUTH_METHOD");')
            self.assertEqual(inventory(root), ([], 0))


if __name__ == '__main__':
    unittest.main()
