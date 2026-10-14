#!/usr/bin/env bash
# The baseline lifecycle smoke with non-demo authentication, validation and Open311 checks.
set -euo pipefail
export SMOKE_DEMO_MODE=false
source "$(dirname "$0")/smoke_common.sh"
python3 - <<'PY'
import json,os,urllib.request,urllib.error
base=os.environ['SMOKE_BASE_URL'];token='test-smoke-dispatch-token'
def request(path,body=None,method=None,auth=False,key=None):
    headers={'Content-Type':'application/json'}
    if auth:headers['Authorization']='Bearer '+token
    if key:headers['Idempotency-Key']=key
    data=None if body is None else json.dumps(body).encode()
    try:
        with urllib.request.urlopen(urllib.request.Request(base+path,data=data,method=method or ('POST' if body is not None else 'GET'),headers=headers),timeout=10) as r:return r.status,json.load(r)
    except urllib.error.HTTPError as e:
        raw=e.read();return e.code,json.loads(raw) if raw else {}
assert request('/healthz')[1]['demoMode'] is False
assert request('/api/incidents')[0]==401
body={'privacyConsentAccepted':True,'privacyConsentVersion':'civicops-popia-v1','description':'Synthetic burst pipe leaking near the hall','area':'Demo North','contactEmail':'resident@example.invalid','consentToContact':True}
status,response=request('/api/incidents',body,key='smoke-key');assert status==201
case=response['incident'];ref=case['referenceNumber']
assert case['status']=='PendingApproval'
assert request('/api/incidents',body,key='smoke-key')[1]['replayed']
assert request('/api/incidents',{'description':'Synthetic different issue','privacyConsentAccepted':True,'privacyConsentVersion':'civicops-popia-v1'},key='smoke-key')[0]==409
assert request('/api/incidents/'+ref+'/approve',{'approvedBy':'spoofed caller'})[0]==401
status,response=request('/api/incidents/'+ref+'/approve',{'approvedBy':'spoofed caller'},auth=True);assert status==200
assert response['incident']['approval']['approvedBy']=='Configured dispatcher'
assert request('/api/incidents/'+ref+'/assign',{'crewId':'electricity-01','approvedBy':'spoofed caller'},auth=True)[0]==400
assert request('/api/incidents/'+ref+'/assign',{'crewId':'water-01','approvedBy':'spoofed caller','expectedVersion':1},auth=True)[0]==409
assert request('/api/incidents/'+ref+'/assign',{'crewId':'water-01','approvedBy':'spoofed caller'},auth=True)[1]['incident']['status']=='Assigned'
assert request('/api/incidents/'+ref+'/status',{'status':'InProgress','approvedBy':'spoofed caller'},method='PATCH',auth=True)[0]==200
assert request('/open311/v2/requests',[])[0]==400
assert request('/open311/v2/requests',{'service_code':'ROADS','description':'Synthetic ambiguous request near hall','privacy_consent_accepted':True,'privacy_consent_version':'civicops-popia-v1'})[0]==200
services=request('/open311/v2/services')[1]
assert {'WATER','ROADS','ELECTRICITY','WASTE','GENERAL','ELEC.LIVE_WIRE_DOWN','EMERG.FLOOD_RESPONSE'} <= {s['service_code'] for s in services}
assert request('/api/audit/verify',auth=True)[1]['valid']
assert request('/api/demo/seed',{},auth=True)[0]==404
print('PASS: non-demo intake, idempotency/conflict, auth, verified actor, eligible crew, optimistic version, lifecycle, Open311/discovery, audit, demo isolation')
PY
