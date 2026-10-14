#!/usr/bin/env python3
"""Offline gate for repository Markdown links and the blank configuration inventory.

Checks local Markdown/HTML image and link targets, reference destinations and
Markdown heading fragments. External URLs are counted, never contacted; their
availability and claims require dated primary-source review. No third-party tools.
"""
import html
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit


def prose(text, inline=True):
    """Hide code blocks/spans while retaining nested list prose."""
    lines, list_indents = [], []
    fence, fence_indent = None, 0
    paragraph_open, indented_code = False, False
    for line in text.splitlines():
        expanded = line.expandtabs(4)
        indent = len(expanded) - len(expanded.lstrip(" "))
        if fence:
            match = re.fullmatch(r" {0,3}(`{3,}|~{3,})\s*", expanded[fence_indent:])
            if match and match[1][0] == fence[0] and len(match[1]) >= len(fence):
                fence = None
                paragraph_open = False
            lines.append("")
            continue
        if not expanded.strip():
            lines.append("")
            paragraph_open = False
            continue
        while list_indents and indent < list_indents[-1]:
            list_indents.pop()
        base = list_indents[-1] if list_indents else 0
        item = re.match(r"( *)(?:[-+*]|\d{1,9}[.)]) +", expanded)
        if item and (indent <= 3 or list_indents and indent - base < 4):
            list_indents.append(item.end())
            base = item.end()
            indented_code = False
        logical = expanded[base:]
        match = re.match(r"^ {0,3}(`{3,}|~{3,})", logical)
        if match:
            fence, fence_indent = match[1], base
            lines.append("")
            paragraph_open = False
        else:
            # A list's content indentation is prose; four additional spaces
            # start a code block. Indented code cannot interrupt a paragraph.
            code = indent - base >= 4 and (indented_code or not paragraph_open) and not logical.lstrip().startswith("<")
            lines.append("" if code else line)
            indented_code = code
            if not code:
                paragraph_open = True
    result = "\n".join(lines)
    return re.sub(r"(`+).*?\1", "", result) if inline else result


def markdown_unescape(value):
    return html.unescape(re.sub(r"\\([!\"#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])", r"\1", value))


def destination(value):
    value = value.strip()
    if value.startswith("<"):
        end = value.find(">")
        if end < 0:
            return None
        value = value[1:end]
    else:
        value = re.split(r"\s", value, maxsplit=1)[0]
    return markdown_unescape(value)


def bracket_end(text, start):
    """Find a label's closing bracket, retaining balanced literal brackets."""
    depth, index = 1, start + 1
    while index < len(text):
        char = text[index]
        if char == "\\":
            index += 2
            continue
        depth += (char == "[") - (char == "]")
        if depth == 0:
            return index
        index += 1
    return None


def inline_destination(text, start):
    """Read a destination/title without counting title or angle parentheses."""
    index = start
    while index < len(text) and text[index].isspace():
        index += 1
    beginning = index
    if index < len(text) and text[index] == "<":
        index += 1
        while index < len(text) and text[index] != ">":
            if text[index] in "\n<":
                return None
            index += 2 if text[index] == "\\" else 1
        if index >= len(text):
            return None
        index += 1
    else:
        depth = 0
        while index < len(text):
            char = text[index]
            if char == "\\":
                index += 2
                continue
            if char.isspace() or char == ")" and depth == 0:
                break
            if char == "<":
                return None
            depth += (char == "(") - (char == ")")
            index += 1
        if depth:
            return None
    target = destination(text[beginning:index])
    separated = index < len(text) and text[index].isspace()
    while index < len(text) and text[index].isspace():
        index += 1
    if separated and index < len(text) and text[index] in "\"'(":
        closing = ")" if text[index] == "(" else text[index]
        index += 1
        while index < len(text) and text[index] != closing:
            index += 2 if text[index] == "\\" else 1
        if index >= len(text):
            return None
        index += 1
        while index < len(text) and text[index].isspace():
            index += 1
    return (target, index + 1) if index < len(text) and text[index] == ")" else None


def reference_key(value):
    return " ".join(markdown_unescape(value).split()).casefold()


def link_details(text):
    """Return targets and undefined explicit full/collapsed references."""
    text = prose(text)
    found, undefined, references = [], [], {}
    definitions = []
    pattern = r"^ {0,3}\[((?:\\.|[^\]\\\n])+)\]:[ \t]*(?:\n[ \t]*)?([^\n]+)$"
    for match in re.finditer(pattern, text, re.M):
        target = destination(match[2])
        if target is not None:
            references.setdefault(reference_key(match[1]), target)
            definitions.append(target)
    body = re.sub(pattern, "", text, flags=re.M)
    index = 0
    while index < len(body):
        if body[index] == "\\":
            index += 2
            continue
        if body[index] != "[":
            index += 1
            continue
        end = bracket_end(body, index)
        if end is None:
            index += 1
            continue
        label = body[index + 1:end]
        after = end + 1
        if after < len(body) and body[after] == "(":
            parsed = inline_destination(body, after + 1)
            if parsed:
                target, index = parsed
                found.append(target)
                continue
        reference = after
        while reference < len(body) and body[reference].isspace():
            reference += 1
        if reference < len(body) and body[reference] == "[":
            close = bracket_end(body, reference)
            if close is not None:
                key = reference_key(body[reference + 1:close] or label)
                if key in references:
                    found.append(references[key])
                else:
                    undefined.append(key)
                index = close + 1
                continue
        # Undefined shortcut labels are ordinary prose, not broken links.
        key = reference_key(label)
        if key in references:
            found.append(references[key])
        index = after
    found.extend(definitions)
    found.extend(html.unescape(m[2]) for m in re.finditer(r"\b(?:href|src)\s*=\s*(['\"])(.*?)\1", text, re.I))
    return found, undefined


def links(text):
    return link_details(text)[0]


def anchors(text):
    # Match GitHub heading IDs, including duplicates and explicit HTML anchors.
    result, counts = set(), {}
    clean = prose(text, inline=False)
    headings = re.findall(r"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", clean, re.M)
    headings += re.findall(r"^([^\n]+)\n\s{0,3}(?:=+|-+)\s*$", clean, re.M)
    for heading in headings:
        heading = re.sub(r"!?\[([^\]]+)\]\([^)]*\)", r"\1", heading)
        slug = html.unescape(re.sub(r"<[^>]+>", "", heading)).lower()
        slug = re.sub(r"[^\w\- ]", "", slug).replace(" ", "-")
        number = counts.get(slug, 0)
        counts[slug] = number + 1
        result.add(slug + (f"-{number}" if number else ""))
    result.update(m[2] for m in re.finditer(r"\b(?:id|name)\s*=\s*(['\"])(.*?)\1", clean))
    return result


def check_links(root, documents):
    errors, local_count, external_count = [], 0, 0
    for document in documents:
        targets, undefined = link_details(document.read_text(encoding="utf-8"))
        errors.extend(f"{document.relative_to(root)}: undefined reference [{key}]" for key in undefined)
        for target in targets:
            uri = urlsplit(target)
            if uri.scheme or uri.netloc:
                external_count += 1
                continue
            local_count += 1
            path = unquote(uri.path)
            resolved = ((root / path.lstrip("/")) if path.startswith("/") else document.parent / path).resolve() if path else document
            location = f"{document.relative_to(root)} -> {target}"
            if not resolved.is_relative_to(root):
                errors.append(f"{location}: outside repository")
            elif not resolved.exists():
                errors.append(f"{location}: missing target")
            elif uri.fragment and resolved.suffix.lower() == ".md" and unquote(uri.fragment) not in anchors(resolved.read_text(encoding="utf-8")):
                errors.append(f"{location}: missing heading/anchor")
    return errors, local_count, external_count


def inventory(root):
    errors, examples = [], []
    for filename in (root / ".env.example", root / "products/civicops/.env.example"):
        values, previous = {}, ""
        for number, line in enumerate(filename.read_text().splitlines(), 1):
            if line and not line.startswith("#"):
                match = re.fullmatch(r"([A-Za-z_][A-Za-z0-9_]*)=", line)
                if not match:
                    errors.append(f"{filename.relative_to(root)}:{number}: only blank placeholders allowed")
                else:
                    name = match[1]
                    if name in values:
                        errors.append(f"{filename.relative_to(root)}:{number}: duplicate {name}")
                    if not previous.startswith("# "):
                        errors.append(f"{filename.relative_to(root)}:{number}: {name} needs a comment")
                    values[name] = number
            previous = line
        examples.append(values)
    names = set(examples[0])
    if names != set(examples[1]):
        errors.append("Root/product environment inventories differ")
    # Configuration literals, including dynamic OIDC keys and mounted secrets.
    used = set()
    source_files = list((root / "products/civicops").rglob("*.cs"))
    source_files += [p for directory in ("scripts", "deploy", "loadtest") for p in (root / directory).rglob("*") if p.suffix in (".py", ".sh", ".mjs", ".js", ".yml", ".yaml") and not p.name.startswith("test_")]
    for source in source_files:
        if any(part in ("bin", "obj") for part in source.parts) or source.name == "check_docs.py":
            continue
        text = source.read_text()
        used.update(re.findall(r"\b(?:(?:CIVICOPS|ELEVENLABS|ZENDESK|OTEL|ASPNETCORE|POSTGRES)_[A-Z][A-Z0-9_]*|PUBLIC_BASE_URL|AllowedHosts)\b", text))
    # The OIDC prefix is concatenated, and SCRIPT_NONCE is an HttpContext.Items
    # key, not an environment setting (SecurityHeaders.cs).
    # POSTGRES_HOST_AUTH_METHOD appears only in a prohibited trust-mode guard;
    # it is never consumed or supported as a configuration input.
    used.difference_update(("CIVICOPS_OIDC_", "CIVICOPS_SCRIPT_NONCE", "POSTGRES_HOST_AUTH_METHOD"))
    for name in sorted(used - names):
        errors.append(f"Missing environment placeholder: {name}")
    identity = (root / "products/civicops/Services/IdentityOptions.cs").read_text()
    for key in re.findall(r'(?:Read|Claim)\("([A-Z_]+)"', identity):
        if "CIVICOPS_OIDC_" + key not in names:
            errors.append(f"Missing dynamic OIDC setting: {key}")
    secrets = (root / "products/civicops/Services/SecretProvider.cs").read_text().split("public static ISecretProvider Create", 1)[0]
    for name in re.findall(r'"((?:CIVICOPS|ELEVENLABS|ZENDESK)_[A-Z_]+)"', secrets):
        if name + "_FILE" not in names:
            errors.append(f"Missing mounted secret setting: {name}_FILE")
    return errors, len(names)


def main():
    root = Path(__file__).resolve().parents[1]
    paths = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=root).decode().split("\0")
    documents = sorted({root / name for name in paths if name.endswith(".md") and (root / name).is_file()})
    errors, local, external = check_links(root, documents)
    env_errors, count = inventory(root)
    errors += env_errors
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"PASS: {len(documents)} Markdown files; {local} local links/fragments valid; {external} external links counted (offline, not fetched); {count} commented blank placeholders in both inventories.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
