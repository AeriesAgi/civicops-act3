#!/usr/bin/env bash
# Synthetic key/data only; verifies HTTP export, exclusive local output, and offline tamper rejection.
set -euo pipefail
export SMOKE_DEMO_MODE=true
export TMPDIR="${TMPDIR:-/tmp}"
mkdir -p "$TMPDIR"
audit_smoke_target=$(mktemp -d "$TMPDIR/civicops-audit-smoke.XXXXXX")
export SMOKE_AUDIT_SIGNING_KEY=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
export SMOKE_AUDIT_EXPORT_DIR="$audit_smoke_target/exclusive"
export CIVICOPS_STORAGE=file CIVICOPS_DATABASE_URL='' CIVICOPS_DATABASE_URL_FILE=''
source "$(dirname "$0")/smoke_common.sh"
trap 'cleanup_smoke; rm -rf "$audit_smoke_target"' EXIT
export AUDIT_SMOKE_TARGET="$audit_smoke_target"
python3 - <<'PY'
import io, json, os, pathlib, urllib.request, zipfile
from http.cookies import SimpleCookie

base = os.environ['SMOKE_BASE_URL']
cookies = {}
csrf = None

def request(path, body=None, method=None):
    headers = {'Content-Type': 'application/json'}
    if cookies:
        headers['Cookie'] = '; '.join(key + '=' + value for key, value in cookies.items())
    if csrf:
        headers['X-CivicOps-CSRF'] = csrf
    data = None if body is None else json.dumps(body).encode()
    with urllib.request.urlopen(urllib.request.Request(base + path, data=data, method=method, headers=headers), timeout=10) as response:
        for value in response.headers.get_all('Set-Cookie', []):
            parsed = SimpleCookie(); parsed.load(value)
            cookies.update({key: item.value for key, item in parsed.items()})
        return response.read(), response.headers

case = json.loads(request('/api/incidents', {'description': 'Synthetic water leak outside demo hall', 'area': 'Synthetic ward'})[0])
assert case['incident']['referenceNumber']
csrf = json.loads(request('/auth/status')[0])['csrfToken']
request('/auth/demo-login', {'role': 'auditor'})
csrf = json.loads(request('/auth/status')[0])['csrfToken']
archive_bytes, headers = request('/api/audit/export')
assert headers.get_content_type() == 'application/zip'
assert headers['Cache-Control'] == 'no-store'
download = pathlib.Path(os.environ['AUDIT_SMOKE_TARGET']) / 'download'
download.mkdir()
with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
    assert set(archive.namelist()) == {'audit.jsonl', 'manifest.json'}
    archive.extractall(download)
manifest = json.loads((download / 'manifest.json').read_bytes())
assert manifest['entries'] >= 2
assert manifest['tenantId'] == 'default'
assert manifest['signatureAlgorithm'] == 'HMAC-SHA256'
first = json.loads(request('/api/audit/export/local', method='POST')[0])
second = json.loads(request('/api/audit/export/local', method='POST')[0])
assert first['exportId'] != second['exportId']
for result in (first, second):
    path = pathlib.Path(os.environ['SMOKE_AUDIT_EXPORT_DIR']) / result['exportId']
    assert (path / 'COMPLETE').exists()
print('PASS: signed HTTP ZIP and two distinct exclusive local exports; synthetic audit counts and tenant binding.')
PY
"$(dirname "$0")/audit_verify.sh" "$audit_smoke_target/download"
for audit_directory in "$SMOKE_AUDIT_EXPORT_DIR"/*; do
  "$(dirname "$0")/audit_verify.sh" "$audit_directory"
done
python3 - <<'PY'
import os, pathlib
path = pathlib.Path(os.environ['AUDIT_SMOKE_TARGET']) / 'download' / 'audit.jsonl'
data = path.read_bytes()
path.write_bytes(data[:-1])
PY
if "$(dirname "$0")/audit_verify.sh" "$audit_smoke_target/download"; then
  printf 'FAIL: truncated audit export was accepted.\n' >&2
  exit 1
fi
printf 'PASS: offline verification accepted all 3 exports and rejected truncation.\n'
