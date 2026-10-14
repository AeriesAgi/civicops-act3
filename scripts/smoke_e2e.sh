#!/usr/bin/env bash
# Entire simulated resident conversation -> ticket -> human response -> signed webhook -> audit.
set -euo pipefail
source "$(dirname "$0")/smoke_common.sh"
python3 - <<'PY'
import hashlib,hmac,json,os,time,urllib.request,urllib.error
base=os.environ['SMOKE_BASE_URL']
def call(path,body=None,method=None,headers=None):
    data=None if body is None else json.dumps(body,separators=(',',':')).encode()
    req=urllib.request.Request(base+path,data=data,method=method or ('POST' if body is not None else 'GET'),headers={'Content-Type':'application/json',**(headers or {})})
    with urllib.request.urlopen(req,timeout=10) as response:return json.load(response)
health=call('/healthz');assert health['demoMode'] and health['emailMode']=='outbox'
payload={'conversation_id':'sim-smoke-conversation','turn_id':'report-turn-1','description':'Synthetic burst pipe near the demo hall.','area':'Demo East','contact_email':'resident@example.invalid','consent_to_contact':True,'contact_preference':'email','language':'af','channel':'phone'}
created=call('/api/channels/elevenlabs/tools/report_incident',payload)
ref=created['referenceNumber'];assert created['mode']=='simulated' and created['status']=='PendingApproval'
assert call('/api/channels/elevenlabs/tools/report_incident',payload)['replayed']
for _ in range(70):
    case=call('/api/dispatch/incidents/'+ref)
    if case['zendeskTicketId']:break
    time.sleep(.1)
assert case['zendeskTicketId'], 'Simulated Zendesk delivery did not complete'
assert os.listdir(os.path.join(os.environ['CIVICOPS_DATA_DIR'],'zendesk-outbox'))
assert os.listdir(os.path.join(os.environ['CIVICOPS_DATA_DIR'],'email-outbox'))
assert len(case['agentRuns'])>=5 and all(r['fallbackUsed'] for r in case['agentRuns'])
case=call('/api/incidents/'+ref+'/approve',{'approvedBy':'Ignored caller label','expectedVersion':case['version']})['incident']
assert case['approval']['approvedBy']=='Demo supervisor (not signed in, unverified)'
case=call('/api/incidents/'+ref+'/assign',{'crewId':'water-01','approvedBy':'Demo operator'})['incident'];assert case['status']=='Assigned'
case=call('/api/incidents/'+ref+'/status',{'status':'InProgress','approvedBy':'Demo operator'},method='PATCH')['incident']
assert case['status']=='InProgress'
draft=next(x for x in reversed(case['commsDrafts']) if x['state']=='PendingReview')
call('/api/incidents/'+ref+'/comms/'+draft['id']+'/approve',{})
post={'type':'post_call_transcription','data':{'conversation_id':'sim-smoke-conversation','transcript':[{'role':'user','message':'Synthetic private transcript marker'}],'analysis':{'transcript_summary':'Synthetic resident reported a pipe leak.','data_collection_results':{'description':{'value':payload['description']},'area':{'value':'Demo East'}}}}}
raw=json.dumps(post,separators=(',',':')).encode();stamp=str(int(time.time()))
sig=hmac.new(b'test-webhook-secret',stamp.encode()+b'.'+raw,hashlib.sha256).hexdigest()
headers={'ElevenLabs-Signature':f't={stamp},v0={sig}'}
assert call('/api/channels/elevenlabs/post-call',post,headers=headers)['received']
assert call('/api/channels/elevenlabs/post-call',post,headers=headers)['replayed']
case=call('/api/dispatch/incidents/'+ref);assert case['transcriptSummary']=='Synthetic resident reported a pipe leak.'
public=call('/api/incidents/'+ref);encoded=json.dumps(public)
assert all(x not in encoded for x in ['resident@example.invalid','private transcript','contactEmail','rawTranscript','agentRuns'])
assert call('/api/channels/elevenlabs/tools/lookup_case_status',{'conversation_id':'sim-smoke-conversation','turn_id':'status-turn','reference_number':ref})['referenceNumber']==ref
verify=call('/api/audit/verify');assert verify['valid'] and verify['entries']>=8
print('PASS: simulated ElevenLabs -> backend agents (deterministic fallback) -> Zendesk outbox -> verified human approval -> eligible assignment -> status -> approved comms -> signed post-call/replay -> private public lookup -> audit chain')
print('Audit entries verified:',verify['entries'])
PY
