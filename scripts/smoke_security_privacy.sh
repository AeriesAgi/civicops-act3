#!/usr/bin/env bash
# Isolated synthetic HTTP evidence; no external providers or resident data.
set -euo pipefail
export SMOKE_DEMO_MODE=true
source "$(dirname "$0")/smoke_common.sh"
python3 - <<'PY'
import json, os, urllib.request, urllib.error
from http.cookies import SimpleCookie

base = os.environ['SMOKE_BASE_URL']
cookies = {}
csrf = None
checks = 0

def require(condition):
    global checks
    assert condition
    checks += 1

def request(path, body=None, method=None, token=True, raw=None):
    headers = {'Content-Type': 'application/json'}
    if cookies:
        headers['Cookie'] = '; '.join(k + '=' + v for k, v in cookies.items())
    if csrf and token:
        headers['X-CivicOps-CSRF'] = csrf
    data = raw if raw is not None else None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method or ('POST' if data is not None else 'GET'), headers=headers)
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        for value in response.headers.get_all('Set-Cookie', []):
            parsed = SimpleCookie(); parsed.load(value)
            cookies.update({key: item.value for key, item in parsed.items()})
        payload = response.read()
        return response.status, json.loads(payload) if payload else {}, response.headers

status, case, headers = request('/api/incidents', {'description': 'Synthetic pipe leak at demo library; email synthetic@example.invalid',
    'contactEmail': 'synthetic@example.invalid', 'consentToContact': True,
    'privacyConsentAccepted': True, 'privacyConsentVersion': 'civicops-popia-v1'})
require(status == 201)
reference = case['incident']['referenceNumber']
require('unsafe-inline' not in headers['Content-Security-Policy'])
require("frame-ancestors 'none'" in headers['Content-Security-Policy'])
for key in ('X-Content-Type-Options', 'Referrer-Policy', 'Permissions-Policy', 'Cross-Origin-Opener-Policy', 'Cross-Origin-Resource-Policy'):
    require(key in headers)
require(headers['Cache-Control'] == 'no-store')
status, body, headers = request('/api/incidents', raw=b'{')
require(status == 400 and headers.get_content_type() == 'application/problem+json')
status, body, headers = request('/api/incidents', raw=b'x' * 32769)
require(status == 413 and headers.get_content_type() == 'application/problem+json')

csrf = request('/auth/status')[1]['csrfToken']
require(request('/auth/demo-login', {'role': 'admin'})[0] == 200)
csrf = request('/auth/status')[1]['csrfToken']
require(request('/api/admin/privacy/requests', {'referenceNumber': reference, 'action': 'access', 'requestNote': 'Synthetic access request from test resident.'}, token=False)[0] == 400)
status, access, _ = request('/api/admin/privacy/requests', {'referenceNumber': reference, 'action': 'access', 'requestNote': 'Synthetic access request from test resident.'})
require(status == 200 and access['status'] == 'pending-verification')
require(request('/api/admin/privacy/requests/' + access['id'] + '/fulfil', {})[0] == 400)
require(request('/api/admin/privacy/requests/' + access['id'] + '/verify', {'verificationNote': 'Synthetic identity checked against case record; no ID copy retained.'})[0] == 200)
status, exported, headers = request('/api/admin/privacy/requests/' + access['id'] + '/fulfil', {})
require(status == 200 and exported['contact']['contactEmail'] == 'synthetic@example.invalid')
require(headers['Cache-Control'] == 'no-store')
require(headers['Content-Disposition'] == 'attachment; filename=privacy-response.json')

status, erase, _ = request('/api/admin/privacy/requests', {'referenceNumber': reference, 'action': 'anonymisation', 'requestNote': 'Synthetic resident requests local anonymisation.'})
require(status == 200)
require(request('/api/admin/privacy/requests/' + erase['id'] + '/verify', {'verificationNote': 'Synthetic identity verified through separate test channel.'})[0] == 200)
incident = request('/api/dispatch/incidents/' + reference)[1]
require(request('/api/admin/privacy/requests/' + erase['id'] + '/fulfil', {'expectedVersion': incident['version']})[0] == 200)
incident = request('/api/dispatch/incidents/' + reference)[1]
require('synthetic@example.invalid' not in json.dumps(incident))
require(request('/api/audit/verify')[1]['valid'])
require(request('/api/privacy/notice')[1]['informationOfficerState'] == 'owner-pending')
print(f'PASS: security/privacy HTTP smoke; {checks} checks; consent, headers, limits, CSRF, verified access, anonymisation and audit.')
PY
