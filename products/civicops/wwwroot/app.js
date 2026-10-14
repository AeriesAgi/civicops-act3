'use strict';
const i18n = window.CivicOpsI18n;
const t = (key, values = {}) => i18n.text(key, values);
const staff = (key, values = {}) => i18n.text(key, values, 'en');
const pageScriptNonce = document.currentScript?.nonce || '';
const $ = selector => document.querySelector(selector);
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const date = value => new Date(value).toLocaleString('en-ZA', {dateStyle:'medium', timeStyle:'short'});
const state = {view:'resident', token:'', auth:null, authEpoch:0, cases:[], crews:[], selected:null, catalogue:[], metrics:null, ai:null, health:null, channels:null, sosQueue:null, selectedSos:null, lastRefresh:0, refreshing:false, streamAbort:null};
const municipalityPath = location.pathname.match(/^\/m\/[^/]+/)?.[0] || '';
const localPath = path => municipalityPath && /^\/(api|auth|open311)(\/|$)/.test(path) ? municipalityPath + path : path;
const hasPermission = permission => state.auth?.permissions?.includes(permission) === true;
let reportKey = null;
let reportFingerprint = null;
let chatTurn = null;
let chatFingerprint = null;
const conversation = `sim-web-${crypto.randomUUID()}`;
async function api(path, options = {}, dispatch = false) {
  if (!navigator.onLine) throw new Error(t('ui.offlineError'));
  const headers = new Headers(options.headers || {});
  if (options.body) headers.set('Content-Type','application/json');
  if (state.token && (!path.startsWith('/auth/') || path === '/auth/status')) headers.set('Authorization',`Bearer ${state.token}`);
  if (!['GET','HEAD','OPTIONS'].includes((options.method || 'GET').toUpperCase()) && state.auth?.csrfToken) headers.set('X-CivicOps-CSRF',state.auth.csrfToken);
  const response = await fetch(localPath(path),{...options,headers,cache:'no-store',credentials:'same-origin'});
  let body = {}; try { body = await response.json(); } catch { /* Empty responses remain bounded messages. */ }
  if (!response.ok) {
    if (response.status === 409 && dispatch && path.startsWith('/api/incidents/')) { await refresh(); if (body.incident) renderDetail(body.incident); }
    if(i18n.language !== 'en' && !dispatch && /^\/api\/(incidents|channels\/elevenlabs\/simulated-chat)/.test(path)) { const error = new Error(t(response.status === 404 ? 'ui.referenceNotFound' : response.status === 429 ? 'ui.rateLimit' : 'ui.requestFailed', {status:response.status})); error.status=response.status; throw error; }
    const error = new Error(body.error || body.detail || body.message || (response.status === 401 ? 'Sign in as municipal staff or connect a service token.' : response.status === 403 ? 'Your role does not permit this action.' : response.status === 429 ? 'Request limit reached. Please retry in a minute.' : `Request failed (${response.status}).`));
    error.status = response.status; throw error;
  }
  return body;
}
function result(selector, message, good = true, language = ['#report-result','#lookup-result'].includes(selector) ? i18n.language : 'en') { const element = $(selector); element.lang=language; element.textContent = message; element.className = good ? 'success' : 'error'; element.hidden = false; }
function network(stale = false) {
  const element = $('#network');
  const offline = !navigator.onLine;
  element.classList.toggle('stale',offline || stale);
  element.lang = i18n.language;
  element.textContent = offline ? t('ui.networkOffline') : stale ? t('ui.networkStale') : `${t(state.health?.demoMode ? 'ui.networkDemo' : 'ui.connected')}${state.lastRefresh ? ' · ' + t('ui.updated',{time:date(state.lastRefresh)}) : ''}`;
}
function setView(view) {
  if(state.view === 'admin' && view !== 'admin') dismissAdminToken();
  if(view !== 'map') window.CivicOpsMap?.hide();
  state.view = ['resident','control','inbox','integrations','map','agencies','audit','admin'].includes(view) ? view : 'resident';
  document.querySelectorAll('.view').forEach(element => { element.hidden = element.id !== state.view; });
  document.querySelectorAll('[data-view]').forEach(button => { const active = button.dataset.view === state.view; button.classList.toggle('active',active); if (active) button.setAttribute('aria-current','page'); else button.removeAttribute('aria-current'); });
  if (state.view === 'control') refresh();
  if (state.view === 'map') window.CivicOpsMap?.show();
  if (state.view === 'agencies') window.CivicOpsAgencies?.show();
  if (state.view === 'audit') loadAudit();
  if (state.view === 'admin') loadAdmin();
  if (['inbox','integrations'].includes(state.view)) window.CivicOpsChannels?.show();
}
document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => { location.hash = button.dataset.view; setView(button.dataset.view); }));
window.addEventListener('hashchange', () => setView(location.hash.slice(1)));
window.addEventListener('online', () => { network(); if(state.view === 'control') refresh(); });
window.addEventListener('offline', () => { network(); state.streamAbort?.abort(); });

// Resident SOS is deliberately opt-in: no location API is touched until the
// three-second hold has created an alert. A location is then shared once, only
// for that alert's server-enforced follow-up window.
const residentSos = { timer:null, progress:null, startedAt:0, alertId:null, cancelTimer:null };
const residentSosHold = $('#resident-sos-hold');
const residentSosStatus = $('#resident-sos-status');
const residentSosAddressPrompt = $('#resident-sos-location-prompt');
const residentSosAddress = $('#resident-sos-address');
function residentSosMessage(message, good = true) {
  residentSosStatus.textContent = message;
  residentSosStatus.className = good ? 'sos-status success' : 'sos-status error';
}
function residentSosResetHold() {
  if (residentSos.timer) { clearInterval(residentSos.timer); residentSos.timer = null; }
  residentSos.startedAt = 0;
  if (residentSos.progress) residentSos.progress.style.width = '0%';
}
function residentSosStartHold(event) {
  if (event?.pointerType === 'mouse' && event.button !== 0) return;
  if (residentSos.timer || residentSos.alertId || residentSosHold.disabled) return;
  residentSos.startedAt = performance.now();
  residentSosMessage('Keep holding… 3 seconds is required to activate SOS.');
  residentSos.timer = setInterval(() => {
    const progress = Math.min(1, (performance.now() - residentSos.startedAt) / 3000);
    residentSos.progress.style.width = `${Math.round(progress * 100)}%`;
    if (progress >= 1) { residentSosResetHold(); residentSosTrigger(); }
  }, 40);
}
function residentSosStopHold() {
  if (residentSos.timer) { residentSosResetHold(); residentSosMessage('SOS hold cancelled. No alert was sent.'); }
}
async function residentSosTrigger() {
  residentSosHold.disabled = true;
  try {
    const response = await api('/api/v1/sos', {method:'POST', body:JSON.stringify({
      operationId:crypto.randomUUID(), channel:'web', note:null, contact:null,
      addressOrLandmark:null, silent:false, location:null, accuracyMeters:null, batteryPercent:null
    })});
    residentSos.alertId = response.alert?.id;
    if (!residentSos.alertId) throw new Error('The control room did not return an SOS reference.');
    residentSosMessage(response.message || 'SOS alert sent to the municipal control room. A human operator must acknowledge it.');
    const cancel = $('#resident-sos-cancel'); cancel.hidden = false; cancel.focus({preventScroll:true});
    residentSos.cancelTimer = setTimeout(() => { cancel.hidden = true; }, 10000);
    if (response.locationRequired) residentSosAddressPrompt.hidden = false;
    residentSosShareLocation();
  } catch (error) {
    residentSosMessage(error.message, false);
    residentSosHold.disabled = false;
  }
}
async function residentSosShareLocation() {
  if (!residentSos.alertId) return;
  const id = residentSos.alertId;
  if (!navigator.geolocation) { residentSosAddressPrompt.hidden = false; residentSosAddress.focus({preventScroll:true}); return; }
  navigator.geolocation.getCurrentPosition(async position => {
    if (residentSos.alertId !== id) return;
    try {
      await api('/api/v1/sos/' + encodeURIComponent(id) + '/location', {method:'POST', body:JSON.stringify({
        operationId:crypto.randomUUID(), location:{longitude:position.coords.longitude, latitude:position.coords.latitude},
        accuracyMeters:Number.isFinite(position.coords.accuracy) ? position.coords.accuracy : null, addressOrLandmark:null
      })});
      residentSosAddressPrompt.hidden = true;
      residentSosMessage('Location shared for this active SOS window only. The control room has your alert.');
    } catch (error) { residentSosMessage(error.message, false); residentSosAddressPrompt.hidden = false; residentSosAddress.focus({preventScroll:true}); }
  }, () => { residentSosAddressPrompt.hidden = false; residentSosMessage('SOS is active. Share an address or landmark so the control room can locate you.'); residentSosAddress.focus({preventScroll:true}); }, {enableHighAccuracy:false, maximumAge:0, timeout:10000});
}
residentSosHold.addEventListener('pointerdown', residentSosStartHold);
residentSosHold.addEventListener('pointerup', residentSosStopHold);
residentSosHold.addEventListener('pointercancel', residentSosStopHold);
residentSosHold.addEventListener('pointerleave', residentSosStopHold);
residentSosHold.addEventListener('keydown', event => { if ((event.key === ' ' || event.key === 'Enter') && !event.repeat) { event.preventDefault(); residentSosStartHold(event); } });
residentSosHold.addEventListener('keyup', event => { if (event.key === ' ' || event.key === 'Enter') residentSosStopHold(); });
residentSos.progress = $('#resident-sos-progress');
$('#resident-sos-address-submit').addEventListener('click', async () => {
  const address = residentSosAddress.value.trim();
  if (!address || !residentSos.alertId) { residentSosAddress.focus(); return; }
  const button = $('#resident-sos-address-submit'); button.disabled = true;
  try {
    await api('/api/v1/sos/' + encodeURIComponent(residentSos.alertId) + '/location', {method:'POST', body:JSON.stringify({operationId:crypto.randomUUID(), location:null, accuracyMeters:null, addressOrLandmark:address})});
    residentSosAddressPrompt.hidden = true; residentSosMessage('Your landmark was shared for this active SOS window. The control room has your alert.');
  } catch (error) { residentSosMessage(error.message, false); }
  finally { button.disabled = false; }
});
$('#resident-sos-cancel').addEventListener('click', async event => {
  if (!residentSos.alertId) return;
  const button = event.currentTarget; button.disabled = true;
  try {
    await api('/api/v1/sos/' + encodeURIComponent(residentSos.alertId) + '/cancel', {method:'POST', body:JSON.stringify({operationId:crypto.randomUUID(), note:'Resident cancelled during the confirmation window.'})});
    residentSosMessage('SOS alert cancelled. No dispatch action was taken.'); residentSosAddressPrompt.hidden = true; residentSos.alertId = null; button.hidden = true; residentSosHold.disabled = false;
  } catch (error) { residentSosMessage(error.message, false); button.disabled = false; }
});
$('#report-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = event.currentTarget; const button = form.querySelector('button[type="submit"]'); if(button.disabled) return; button.disabled = true;
  const fields = new FormData(form); const request = {description:fields.get('description'),area:fields.get('area'),residentLanguage:fields.get('residentLanguage'),contactEmail:fields.get('contactEmail') || null,consentToContact:fields.has('consentToContact'),contactPreference:fields.has('consentToContact') ? 'email' : 'none',privacyConsentAccepted:fields.has('privacyConsentAccepted'),privacyConsentVersion:'civicops-popia-v1'};
  const fingerprint = JSON.stringify(request);
  if(!reportKey || fingerprint !== reportFingerprint) { reportKey = crypto.randomUUID(); reportFingerprint = fingerprint; }
  try { const response = await api('/api/incidents',{method:'POST',body:fingerprint,headers:{'Idempotency-Key':reportKey}}); result('#report-result',t('ui.reportSuccess',{reference:response.incident.referenceNumber})); $('#reference').value = response.incident.referenceNumber; form.reset(); $('#language').value = i18n.language; reportKey = null; reportFingerprint = null; }
  catch(error) { result('#report-result',error.message,false); }
  finally { button.disabled = false; }
});
$('#lookup-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = event.currentTarget.querySelector('button'); if(button.disabled) return; button.disabled = true;
  try { const incident = await api('/api/incidents/' + encodeURIComponent($('#reference').value.trim())); result('#lookup-result',`${incident.referenceNumber} · ${i18n.text('status.' + incident.status,{},incident.residentLanguage)}. ${incident.publicUpdate}`,true,incident.residentLanguage); }
  catch(error) { result('#lookup-result',error.status === 404 ? t('ui.referenceNotFound') : error.message,false); }
  finally { button.disabled = false; }
});
function bubble(message, role = 'agent', language = i18n.language) { const element = document.createElement('p'); element.className = 'bubble ' + role; element.lang=language; element.textContent = message; $('#chat-log').append(element); $('#chat-log').scrollTop = $('#chat-log').scrollHeight; }
$('#chat-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = event.currentTarget.querySelector('button'); if(button.disabled) return; button.disabled = true;
  const message = $('#chat-message').value.trim(); const reference = message.match(/CIV-\d{8}-[A-F0-9]{8}/i)?.[0];
  const payload = {language:i18n.language,conversation_id:conversation,turn_id:chatTurn || crypto.randomUUID(),description:message,area:$('#chat-area').value.trim() || t('ui.areaNeedsConfirmation'),reference_number:reference,channel:'elevenlabs-chat',privacy_consent_accepted:$('#chat-privacy-consent').checked,privacy_consent_version:'civicops-popia-v1'};
  if(!reference && !payload.privacy_consent_accepted) { bubble(t('ui.chatAcceptPrivacy')); button.disabled = false; return; }
  const fingerprint = message + '|' + payload.area + '|' + payload.privacy_consent_accepted + '|' + payload.language;
  if(!chatTurn || chatFingerprint !== fingerprint) { chatTurn = crypto.randomUUID(); chatFingerprint = fingerprint; }
  payload.turn_id = chatTurn;
  bubble(message,'resident');
  try { const response = reference ? await api('/api/incidents/' + encodeURIComponent(reference)) : await api('/api/channels/elevenlabs/simulated-chat', {method:'POST',body:JSON.stringify(payload),headers:{'X-CivicOps-Conversation-Id':conversation}}); bubble(`${response.referenceNumber || reference || ''}. ${response.message || response.update || response.publicUpdate || response.nextStep || t('update.PendingApproval')}`, 'agent', response.residentLanguage || i18n.language); $('#chat-message').value = ''; chatTurn = null; chatFingerprint = null; if(response.referenceNumber) $('#reference').value = response.referenceNumber; }
  catch(error) { bubble(error.message + ' ' + t('ui.retrySafely')); }
  finally { button.disabled = false; }
});
$('#access-form').addEventListener('submit', async event => { event.preventDefault(); state.token = $('#dispatch-token').value.trim(); $('#dispatch-token').value = ''; clearStaffData(); try { await loadIdentity(); await refresh(); if(hasPermission('operations-read')) startStream(); } catch(error) { result('#control-error',error.message,false); } });
$('#refresh').addEventListener('click',refresh);
$('#seed').addEventListener('click', async event => { event.currentTarget.disabled = true; try { await api('/api/demo/seed',{method:'POST'},true); await refresh(); } catch(error) { result('#control-error',error.message,false); } finally { event.currentTarget.disabled = false; } });
for (const id of ['department-filter','priority-filter','status-filter']) $('#' + id).addEventListener('change',renderQueue);
function kpi(label,value,detail,graphic = '') { return `<article class="kpi"><span class="label">${esc(label)}</span><strong>${esc(value)}</strong><small>${esc(detail)}</small>${graphic}</article>`; }
function duration(milliseconds) {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return staff('staff.dynamic.not_recorded_b37c7879');
  return milliseconds < 1 ? '<1 ms' : `${milliseconds.toFixed(milliseconds < 10 ? 1 : 0)} ms`;
}
function metricBar(value,split = false) {
  if (!Number.isFinite(value)) return '';
  return `<svg class="${split ? 'metric-split' : 'metric-bar'}" viewBox="0 0 100 4" preserveAspectRatio="none" aria-hidden="true"><rect class="metric-track" width="100" height="4"/><rect class="metric-fill" width="${Math.min(100,Math.max(0,value*100)).toFixed(1)}" height="4"/></svg>`;
}
function renderKpis() {
  const m = state.metrics;
  const seconds = item => item?.medianSeconds == null ? staff('staff.dynamic.pending_331551b0') : item.medianSeconds < .1 ? '<0.1 s' : `${item.medianSeconds.toFixed(1)} s`;
  const share = value => value == null ? staff('staff.dynamic.pending_331551b0') : `${Math.round(value*100)}%`;
  const samples = item => `${item.samples} recorded ${item.samples === 1 ? 'milestone' : 'milestones'} · median`;
  $('#outcome-metrics').innerHTML = m ? kpi(staff('staff.dynamic.report_triage_3e86ef92'), seconds(m.reportToTriage), samples(m.reportToTriage)) + kpi(staff('staff.dynamic.report_approval_45cc8fa4'), seconds(m.reportToApproval), samples(m.reportToApproval)) + kpi(staff('staff.dynamic.report_assignment_3a5872c8'), seconds(m.reportToAssignment), samples(m.reportToAssignment)) + kpi(staff('staff.dynamic.report_resolution_cca17dd7'), seconds(m.reportToResolution), samples(m.reportToResolution)) + kpi(staff('staff.dynamic.digital_intake_share_26765e60'), share(m.withoutPhoneQueueShare), staff('staff.dynamic.web_open311_chat_queue_proxy_d2e27fd4'),metricBar(m.withoutPhoneQueueShare)) + kpi(staff('staff.dynamic.ai_fallback_decisions_1b44958f'), `${m.aiDecisions} / ${m.fallbackDecisions}`, staff('staff.dynamic.recorded_intake_reasoning_steps_82858f2e'),metricBar(m.aiDecisions+m.fallbackDecisions ? m.aiDecisions/(m.aiDecisions+m.fallbackDecisions) : null,true)) + kpi(staff('staff.dynamic.duplicate_candidate_rate_0b344b32'), share(m.duplicateRate), staff('staff.dynamic.flagged_for_human_review_a63579d5'),metricBar(m.duplicateRate)) : `<p class="empty">${staff('staff.dynamic.measured_outcomes_appear_after_reports_enter_the_workfl_c34dcd82')}</p>`;
  $('#metrics-method').textContent = m?.method || '';
  const dataLabel = $('#metrics-data-label');
  if(dataLabel) dataLabel.textContent = m?.syntheticData ? staff('staff.dynamic.synthetic_demo_data_49949bf8') : staff('staff.dynamic.recorded_workflow_data_fd18d44d');

  const open = state.cases.filter(x => !['Resolved','Closed'].includes(x.status)); const pending = open.filter(x => x.needsHumanReview); const breaches = pending.filter(x => Date.parse(x.slaDueAt) < Date.now());
  $('#kpis').innerHTML = kpi(staff('staff.dynamic.open_service_reports_67b00759'),open.length,`${open.filter(x=>x.priority==='Urgent').length} urgent hazards`) + kpi(staff('staff.dynamic.awaiting_human_review_636b65af'),pending.length,staff('staff.dynamic.no_automatic_dispatch_1f24267f')) + kpi(staff('staff.dynamic.review_target_breaches_4cf4d3a0'),breaches.length,staff('staff.dynamic.catalogue_review_targets_15db3733')) + kpi(staff('staff.dynamic.agent_decision_p95_440edc2f'),state.ai?.callCount ? duration(state.ai.p95LatencyMs) : staff('staff.dynamic.pending_331551b0'),state.ai?.providerLabel || staff('staff.dynamic.connecting_d403c686')) + kpi(staff('staff.dynamic.deterministic_fallback_01c88135'),state.ai?.callCount ? `${Math.round(state.ai.fallbackRate*100)}%` : staff('staff.dynamic.pending_331551b0'),`${state.ai?.callCount || 0} recorded reasoning calls`);
}
function renderQueue() {
  const cases = state.cases.filter(x => (!$('#department-filter').value || x.department === $('#department-filter').value) && (!$('#priority-filter').value || x.priority === $('#priority-filter').value) && (!$('#status-filter').value || x.status === $('#status-filter').value)).sort((a,b)=>({Urgent:0,High:1,Medium:2,Low:3}[a.priority]-{Urgent:0,High:1,Medium:2,Low:3}[b.priority]) || Date.parse(a.createdAt)-Date.parse(b.createdAt));
  $('#queue-count').textContent = cases.length;
  $('#queue').innerHTML = cases.length ? cases.map(x => `<button class="queue-item ${x.id === state.selected?.id ? 'selected' : ''}" data-case="${esc(x.id)}"><div class="row"><span class="case-ref">${esc(x.referenceNumber)}</span><span class="pill ${x.priority.toLowerCase()}">${esc(x.priority)}</span></div><h3>${esc(x.intakeSummary || x.description)}</h3><div class="meta"><span>${esc(x.department)} · ${esc(x.area)}</span><span>${esc(x.status)}</span></div><div class="meta"><span>${esc(x.source)} · ${esc(x.residentLanguage)}</span><span class="${x.needsHumanReview && Date.parse(x.slaDueAt)<Date.now() && !['Resolved','Closed'].includes(x.status) ? 'sla-breach' : ''}">Review target ${esc(date(x.slaDueAt))}${x.approval && !x.needsHumanReview ? " · reviewed" : ""}</span></div></button>`).join('') : `<p class="empty">${staff('staff.dynamic.no_cases_match_this_queue_report_an_issue_or_load_the_s_c392efc0')}</p>`;
  $('#queue').querySelectorAll('[data-case]').forEach(button => button.addEventListener('click', () => { const incident = state.cases.find(x=>x.id===button.dataset.case); renderDetail(incident); renderQueue(); }));
  const counts = {}; for(const x of state.cases) { const channel=staff('staff.dynamic.channel_b7c0933e')+x.source; counts[channel]=(counts[channel] || 0)+1; if(!['Resolved','Closed'].includes(x.status)) { for(const key of [staff('staff.dynamic.open_a023a869')+x.department,staff('staff.dynamic.open_a023a869')+x.priority]) counts[key]=(counts[key] || 0)+1; } }
  $('#distribution').innerHTML = Object.entries(counts).map(([key,value]) => `<span>${esc(key)} · ${value}</span>`).join('');
}
function renderAgentPipeline(runs) {
  const names = {'IntakeRoom.Coordinator':'Intake room · coordinator seat (classifier)','IntakeRoom.Extractor':'Intake room · implementer seat (location/hazards)','IntakeRoom.Reviewer':'Intake room · reviewer seat (safety)','IntakeRoom.Recommendation':'Intake room · recommendation (advisory)',IntakeAgent:'Intake',TriageAgent:'Triage',TriageSafetyFloor:staff('staff.dynamic.hazard_safety_floor_27659c65'),DuplicateAgent:staff('staff.dynamic.duplicate_check_3039e724'),DispatchAgent:staff('staff.dynamic.crew_recommendation_1e33ad1f'),CommsAgent:staff('staff.dynamic.resident_update_draft_6b23d6b3')};
  const renderRun = (run,index) => {
    const review = ['IntakeAgent','TriageAgent'].includes(run.agentName) && run.confidence < .75;
    const status = review ? staff('staff.dynamic.review_required_f0742e3a') : run.error ? staff('staff.dynamic.fallback_applied_da85be0a') : run.agentName === 'TriageSafetyFloor' ? staff('staff.dynamic.safety_applied_8cd5fe8c') : staff('staff.dynamic.recorded_c7175fa7');
    const confidence = !run.fallbackUsed && ['IntakeAgent','TriageAgent','DuplicateAgent'].includes(run.agentName) && Number.isFinite(run.confidence) ? `<p class="hint">Model-reported confidence: ${Math.round(run.confidence*100)}% · uncalibrated; human review remains required.</p>` : '';
    return `<details class="agent-run" data-run-index="${index}" data-run-id="${esc(run.id)}"><summary class="agent-step"><span class="agent-name"><span class="step-index" aria-hidden="true">${String(index+1).padStart(2,'0')}</span><span title="${esc(run.agentName)}">${esc(names[run.agentName] || run.agentName)}</span></span><span class="agent-provider"><span>${esc(run.fallbackUsed ? (String(run.error||'').includes('llm_cap_reached') ? SPEND_CAP_LABEL : staff('staff.dynamic.deterministic_fallback_01c88135')) : run.provider)}</span><small>${esc(run.model)}${run.fallbackUsed ? ' · offline' : ''}</small></span><span class="agent-duration">${esc(duration(run.latencyMs))}</span><span class="agent-status status-badge ${review || run.error ? 'warned' : 'eligible'}">${status}</span></summary><div class="agent-reasoning"><span class="provider">${run.fallbackUsed ? staff('staff.dynamic.deterministic_fallback_no_model_inference_used_for_this_62712e75') : staff('staff.dynamic.model_response_provider_and_hardware_claims_unverified_910c4964')}</span><p>${esc(run.rationale)}</p>${confidence}<p class="hint">Permitted tools: ${esc((run.toolPermissions || []).join(', ') || staff('staff.dynamic.none_dc937b59'))}${run.error ? ' · Fallback reason: ' + esc(run.error) : ''}</p><p class="hint"><time datetime="${esc(run.at)}">${esc(date(run.at))}</time> · ${staff('staff.pipeline.runId')} ${esc(run.id)}</p></div></details>`;
  };
  const drafts = runs.map((run,index) => ({run,index})).filter(item => item.run.agentName === 'CommsAgent');
  const renderDrafts = () => {
    const first = drafts[0];
    const uniformProvider = drafts.every(({run}) => run.provider === first.run.provider && run.model === first.run.model && run.fallbackUsed === first.run.fallbackUsed);
    const provider = uniformProvider ? (first.run.fallbackUsed ? staff('staff.dynamic.deterministic_fallback_01c88135') : first.run.provider) : staff('staff.pipeline.mixedProviders');
    const model = uniformProvider ? first.run.model + (first.run.fallbackUsed ? ' · offline' : '') : staff('staff.pipeline.expandProviders');
    const measured = drafts.every(({run}) => Number.isFinite(run.latencyMs) && run.latencyMs >= 0);
    const total = measured ? duration(drafts.reduce((sum,{run}) => sum + run.latencyMs,0)) : staff('staff.dynamic.not_recorded_b37c7879');
    return `<details class="agent-run-group" data-draft-count="${drafts.length}"><summary class="agent-step"><span class="agent-name"><span class="step-index" aria-hidden="true">${String(first.index+1).padStart(2,'0')}</span><span>${staff('staff.pipeline.residentUpdateDrafts')}</span></span><span class="agent-provider"><span>${esc(provider)}</span><small>${esc(model)}</small></span><span class="agent-duration" title="${esc(staff('staff.pipeline.totalDuration'))}">${esc(total)}<small>${staff('staff.pipeline.total')}</small></span><span class="agent-status status-badge ${drafts.some(({run}) => run.error) ? 'warned' : 'eligible'}">${staff('staff.pipeline.draftCount',{count:drafts.length})}</span></summary><div class="agent-draft-history"><p class="hint">${staff('staff.pipeline.historyHint')}</p>${drafts.map(({run,index}) => renderRun(run,index)).join('')}</div></details>`;
  };
  const rows = runs.map((run,index) => run.agentName !== 'CommsAgent' || drafts.length < 2 ? renderRun(run,index) : index === drafts[0].index ? renderDrafts() : '').join('');
  return `<section id="agent-pipeline" class="pipeline-section"><h3>${staff('staff.dynamic.agent_provenance_e9148e65')}</h3><p class="hint">${staff('staff.dynamic.recorded_decisions_with_reasoning_available_for_review__ce61b136')}</p><div class="pipeline-head" aria-hidden="true"><span>${staff('staff.dynamic.step_8e6a6cca')}</span><span>${staff('staff.dynamic.provider_model_51686a90')}</span><span>${staff('staff.dynamic.duration_4fc52a3c')}</span><span>${staff('staff.dynamic.status_920e413c')}</span></div>${runs.length ? rows : `<p class="empty">${staff('staff.dynamic.no_recorded_agent_decisions_for_this_case_87caf31f')}</p>`}</section>`;
}
const LEGACY_LABELS = {'zendesk-delivered':'case-record-synced','zendesk-webhook':'case-record-update','Zendesk integration':'CivicOps case record agent','Verified Zendesk webhook':'Verified case record update'};
function displayName(value) { return LEGACY_LABELS[value] || value; }
function timelineLabel(action) {
  const keys = {received:'staff.dynamic.report_received_6b10cb5b',triaged:'staff.dynamic.triage_recorded_6c0c83bb','agent-pipeline':'staff.dynamic.agent_decisions_recorded_77b71f79',approved:'staff.dynamic.human_response_approved_13162098',reapproved:'staff.timeline.responseReviewed',assigned:'staff.dynamic.crew_assigned_d279da3f',status:'staff.dynamic.case_status_updated_d561ff59',resolved:'staff.timeline.caseResolved',closed:'staff.timeline.caseClosed','comms-approved':'staff.dynamic.resident_update_approved_5eda3480','work-order-type-overridden':'staff.dynamic.routing_override_recorded_8828e60c','zendesk-delivered':'staff.timeline.zendeskSynced','case-record-synced':'staff.timeline.zendeskSynced','zendesk-webhook':'staff.timeline.zendeskReceived','metadata-updated':'staff.timeline.metadataUpdated','tenant.migrated':'staff.timeline.municipalityAssigned','elevenlabs-call-failed':'staff.timeline.callFailed','elevenlabs-danger-followup':'staff.timeline.dangerFollowup','elevenlabs-callback':'staff.timeline.callbackStarted','elevenlabs-callback-uncertain':'staff.timeline.callbackUncertain','privacy.contact-separated':'staff.timeline.contactSeparated','privacy.contact-purged':'staff.timeline.contactPurged','privacy.access':'staff.timeline.privacyAccess','privacy.correction':'staff.timeline.privacyCorrection','privacy.anonymisation':'staff.timeline.privacyAnonymisation'};
  if(keys[action]) return staff(keys[action]);
  if(action.startsWith('elevenlabs-tool-')) return staff('staff.timeline.conversationUpdated');
  if(action.startsWith('elevenlabs-post-call-')) return staff('staff.timeline.callSummary');
  if(action.startsWith('elevenlabs-callback-')) return staff('staff.timeline.callbackRequested');
  return staff('staff.timeline.otherEvent');
}
function renderDecisionTimeline(audit) {
  const rows = [];
  for(let index=0;index<audit.length;index++) {
    const event = audit[index];
    if(event.action === 'metrics-intake') continue;
    const next = audit[index+1];
    // Keep both durable audit entries; present the paired workflow milestone once.
    const paired = event.action === 'status' && ['resolved','closed'].includes(next?.action) && event.actor === next.actor;
    const sources = paired ? [event,audit[++index]] : [event];
    const milestone = sources[sources.length-1];
    const details = [...new Set(sources.map(source => source.detail))].map(detail => `<p>${esc(detail)}</p>`).join('');
    const history = paired ? `<details class="timeline-source"><summary>${staff('staff.timeline.auditRecordCount',{count:sources.length})}</summary><ol>${sources.map(source => `<li><span>${staff('staff.timeline.eventCode')} <code>${esc(displayName(source.action))}</code></span><p>${esc(source.detail)}</p><span>${esc(displayName(source.actor))} · <time datetime="${esc(source.at)}">${esc(date(source.at))}</time></span></li>`).join('')}</ol></details>` : '';
    rows.push(`<article class="timeline-item" data-event-actions="${esc(sources.map(source => source.action).join(' '))}"><h3>${esc(timelineLabel(milestone.action))}</h3>${details}<span class="hint">${esc(displayName(milestone.actor))} · <time datetime="${esc(milestone.at)}">${esc(date(milestone.at))}</time></span>${history}</article>`);
  }
  return rows.join('') || `<p class="empty">${staff('staff.dynamic.decisions_appear_here_as_this_case_progresses_de4a0902')}</p>`;
}
function renderCaseEvidence(incident,ticket) {
  return `<div id="case-evidence" class="case-evidence"><div class="evidence-header"><span class="eyebrow">CASE EVIDENCE · ${esc(incident.referenceNumber)}</span><span class="evidence-ticket">Case · ${ticket}</span></div>${renderAgentPipeline(incident.agentRuns || [])}<section id="decision-timeline" class="decision-section"><h3>${staff('staff.dynamic.decision_timeline_0b4773da')}</h3><div class="timeline">${renderDecisionTimeline(incident.audit || [])}</div></section></div>`;
}
function renderCrewReport(report,id) {
  if (!report) return '';
  const complete = report.status === 'complete';
  return `<section class="crew-work-report" aria-label="Crew work report"><h3 class="case-section-heading">Crew work report</h3><p><span class="pill muted">${esc(report.status)}</span> ${esc(report.reportedBy || 'Assigned crew')} · ${report.reportedAt ? esc(date(report.reportedAt)) : 'Work evidence received'}</p><p class="hint">${complete ? 'Crew completion reported. A dispatcher must review the work and resolve the case through the existing human workflow. ' : ''}${Number(report.noteCount) || 0} notes · ${Number(report.photoCount) || 0} sanitized photos. No automated dispatch or resident notification.</p>${hasPermission('supervise') ? `<button type="button" class="secondary" data-crew-review="${esc(id)}">Review crew notes and photos</button><div id="crew-report-evidence" aria-live="polite"></div>` : ''}</section>`;
}
async function reviewCrewEvidence(id) {
  const evidence = $('#crew-report-evidence');
  try {
    const response = await api('/api/v1/crew/jobs/'+encodeURIComponent(id),{},true);
    if (state.selected?.id !== id || !evidence?.isConnected) return;
    const job = response.job || response;
    evidence.innerHTML=`<h4>Worker notes</h4>${(job.notes || []).map(note=>`<p>${esc(note.text)} <small>${esc(date(note.createdAt))}</small></p>`).join('') || '<p class="hint">No retained work notes.</p>'}<h4>Sanitized work photos</h4>${(job.photos || []).map(photo=>`<button class="secondary" type="button" data-crew-photo="${esc(photo.id)}">Download photo · ${esc(date(photo.createdAt))} · ${Math.round(photo.sizeBytes / 1024)} KB</button>`).join('') || '<p class="hint">No retained photos.</p>'}<p class="hint">Access is audited. Store downloads privately; photo metadata has been removed.</p>`;
    evidence.querySelectorAll('[data-crew-photo]').forEach(button=>button.addEventListener('click',async()=>{
      button.disabled=true;
      try {
        const headers=state.token ? {Authorization:'Bearer '+state.token} : {};
        const result=await fetch(localPath('/api/v1/crew/jobs/'+encodeURIComponent(id)+'/photos/'+encodeURIComponent(button.dataset.crewPhoto)),{headers,credentials:'same-origin',cache:'no-store'});
        if (!result.ok) throw new Error('Photo is unavailable or your review access ended ('+result.status+').');
        const type=result.headers.get('Content-Type')?.split(';')[0];
        if (!['image/png','image/jpeg'].includes(type)) throw new Error('Unexpected evidence format.');
        const url=URL.createObjectURL(await result.blob());const link=document.createElement('a');
        link.href=url;link.download='civicops-work-evidence.'+(type==='image/png'?'png':'jpg');link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
      } catch(failure) { resultMessage(failure.message); }
      finally { if(button.isConnected)button.disabled=false; }
    }));
  } catch(failure) { if(evidence?.isConnected)evidence.textContent=failure.message; }
  function resultMessage(text) { if(evidence?.isConnected) {const message=document.createElement('p');message.className='error';message.textContent=text;evidence.append(message);} }
}
function renderDetail(incident) {
  if(!incident) return;
  // Access auditing refreshes the case. Keep the reviewed evidence and its in-flight
  // download handlers while the same work report is still current.
  const previousEvidence = state.selected?.id === incident.id &&
    JSON.stringify(state.selected.crewReport) === JSON.stringify(incident.crewReport) &&
    hasPermission('supervise') ? $('#crew-report-evidence') : null;
  const focusedEvidence = previousEvidence?.contains(document.activeElement) ? document.activeElement : null;
  state.selected = incident;
  const needsSupervisor = incident.priority === 'Urgent' || ['high','critical'].includes(incident.workOrderType?.hazardClass);
  const eligible = []; // The server evaluates current worker skills and certificate expiry.
  const transitions = {PendingApproval:['Closed'],Approved:['Closed'],Assigned:['InProgress','Closed'],InProgress:['Resolved','WaitingForCitizen','Closed'],WaitingForCitizen:['InProgress','Resolved','Closed'],Resolved:['Closed','InProgress'],Closed:[]};
  const actions = `${(['PendingApproval','Triaged'].includes(incident.status) || incident.needsHumanReview && incident.status !== 'Closed') ? `<button class="primary" data-action="approve">${incident.approval ? staff('staff.dynamic.review_new_information_aa441b66') : staff('staff.dynamic.approve_response_48df5572')}</button>` : ''}${incident.status === 'Approved' ? `<select id="crew-choice" aria-label="${staff('staff.dynamic.eligible_crew_54caaff8')}">${eligible.map(x=>`<option value="${esc(x.id)}">${esc(x.name)}</option>`).join('')}</select><button class="primary" data-action="assign" disabled>${staff('staff.dynamic.assign_crew_161584fe')}</button>` : ''}${(transitions[incident.status] || []).map(status=>`<button class="secondary" data-status="${status}">${status === 'InProgress' ? staff('staff.dynamic.start_response_59de546d') : status === 'Resolved' ? staff('staff.dynamic.mark_resolved_d6d8eedd') : status === 'Closed' ? staff('staff.dynamic.close_case_dd2b37d1') : staff('staff.dynamic.request_information_864974be')}</button>`).join('')}${incident.callbackConsent && incident.consentToContact && state.channels?.elevenlabs?.callbacks !== 'disabled' ? `<button class="secondary" data-action="callback">${staff('staff.dynamic.request_consented_callback_4f7841f2')}</button>` : ''}`;
  const ticket = incident.zendeskTicketUrl && /^https:\/\/[a-z0-9-]+\.zendesk\.com\//.test(incident.zendeskTicketUrl) ? `<a href="${esc(incident.zendeskTicketUrl)}" target="_blank" rel="noopener noreferrer">Ticket ${esc(incident.zendeskTicketId)}</a>` : incident.zendeskTicketId ? `Simulated ticket ${esc(incident.zendeskTicketId)}` : staff('staff.dynamic.delivery_queued_01504f45');
  $('#case-detail').innerHTML = `<div class="detail-head"><span class="eyebrow">CASE WORKSPACE · VERSION ${incident.version}</span><h2>${esc(incident.referenceNumber)}</h2><span class="pill ${incident.priority.toLowerCase()}">${esc(incident.priority)}</span> <span class="pill muted">${esc(incident.status)}</span></div><p class="case-description">${esc(incident.description)}</p><div class="detail-summary"><div><span>${staff('staff.dynamic.department_area_f2a57198')}</span>${esc(incident.department)}<br>${esc(incident.area)}</div><div><span>${staff('staff.dynamic.channel_language_0d9140c0')}</span>${esc(incident.source)} · ${esc(incident.residentLanguage)}</div><div><span>${staff('staff.dynamic.human_approval_1dc716cf')}</span>${esc(incident.approval?.approvedBy || staff('staff.dynamic.pending_331551b0'))}</div><div><span>${staff('staff.dynamic.zendesk_12e5f202')}</span>${ticket}</div></div>${incident.duplicateOf ? `<p class="error">Possible duplicate of ${esc(incident.duplicateOf)} (${Math.round((incident.duplicateConfidence || 0)*100)}%). Human review required; cases remain separate.</p>` : ''}<p class="hint">Recommended crew: ${esc(incident.recommendedCrewId || staff('staff.dynamic.escalate_to_dispatcher_4f19072d'))}. ${esc(incident.recommendedCrewReason)}</p>${renderCrewReport(incident.crewReport,incident.id)}<div class="detail-actions">${actions}</div><div id="action-result" role="status"></div><h3 class="case-section-heading">${staff('staff.dynamic.work_order_amp_crew_qualifications_d8acc478')}</h3><p><strong>${esc(incident.workOrderTypeCode)}</strong> · ${esc(incident.workOrderType?.title)} · ${esc(incident.workOrderType?.hazardClass)} hazard</p><p class="hint">PPE: ${esc((incident.workOrderType?.ppe || []).join(', '))}. Catalogue target: ${esc(incident.workOrderType?.slaTargetMinutes)} minutes; no arrival commitment.</p><details class="procedure-details"><summary>${staff('staff.dynamic.typical_procedure_bdfe7b44')}</summary><ol>${(incident.workOrderType?.procedureSteps || []).map(step=>`<li>${esc(step)}</li>`).join('')}</ol></details><div id="crew-eligibility" aria-live="polite">${staff('staff.dynamic.checking_current_crew_qualifications_7ed4a5d6')}</div>${incident.status !== 'Closed' ? `<details class="routing-override"><summary>${staff('staff.dynamic.dispatcher_routing_override_8cbd603c')}</summary><label for="type-choice">${staff('staff.dynamic.work_order_type_3a1546db')}</label><select id="type-choice">${state.catalogue.map(type=>`<option value="${esc(type.code)}" ${type.code === incident.workOrderTypeCode ? 'selected' : ''}>${esc(type.code)} · ${esc(type.title)}</option>`).join('')}</select><label for="type-note">${staff('staff.dynamic.reason_for_override_5b3e3b72')}</label><input id="type-note" maxlength="2000" placeholder="${staff('staff.dynamic.record_why_this_routing_changes_b34de398')}"><button class="secondary" data-action="work-order-type">${staff('staff.dynamic.override_type_for_review_f7725827')}</button></details>` : ''}<h3 class="case-section-heading">${staff('staff.dynamic.conversation_amp_intake_09ecd3e9')}</h3><p>${esc(incident.transcriptSummary || incident.intakeSummary || staff('staff.dynamic.resident_form_report_no_call_transcript_attached_b3c1feb7'))}</p><h3 class="case-section-heading">${staff('staff.dynamic.resident_communications_review_before_sending_2cee9805')}</h3>${(incident.commsDrafts || []).map(draft=>`<div class="draft"><span class="pill muted">${esc(draft.language)} · ${esc(draft.status)} · ${esc(draft.state)}</span><p>${esc(draft.text)}</p>${draft.state === 'PendingReview' ? `<button class="secondary" data-draft="${esc(draft.id)}">${staff('staff.dynamic.approve_update_3757c835')}</button>` : `<span class="hint">Reviewed by ${esc(draft.approvedBy)}</span>`}</div>`).join('')}${renderCaseEvidence(incident,ticket)}`;
  if(previousEvidence) {
    $('#crew-report-evidence')?.replaceWith(previousEvidence);
    focusedEvidence?.focus({preventScroll:true});
  }
  $('#case-detail [data-crew-review]')?.addEventListener('click', () => reviewCrewEvidence(incident.id));
  $('#case-detail').querySelectorAll('[data-action],[data-status],[data-draft]').forEach(button => button.addEventListener('click', async () => {
    if(button.disabled) return; $('#case-detail').querySelectorAll('button').forEach(x=>x.disabled=true);
    const request = {expectedVersion:incident.version,approvedBy:staff('staff.dynamic.verified_by_server_e2877f7f')};
    let path = `/api/incidents/${encodeURIComponent(incident.id)}/`; let method = 'POST'; const headers = {};
    if(button.dataset.status) { path += 'status'; method = 'PATCH'; request.status = button.dataset.status; }
    else if(button.dataset.draft) path += `comms/${encodeURIComponent(button.dataset.draft)}/approve`;
    else { path += button.dataset.action; if(button.dataset.action === 'assign') request.crewId = $('#crew-choice').value; if(button.dataset.action === 'work-order-type') { request.workOrderTypeCode = $('#type-choice').value; request.note = $('#type-note').value.trim(); } if(button.dataset.action === 'callback') headers['Idempotency-Key'] = `callback-${incident.id}-${incident.version}`; }
    try { const response = await api(path,{method,body:JSON.stringify(request),headers},true); if(response.incident) renderDetail(response.incident); await refresh(); result('#action-result',response.message || staff('staff.dynamic.decision_recorded_in_the_audit_trail_4b028794')); }
    catch(error) { if($('#action-result')) result('#action-result',error.message,false); }
    finally { $('#case-detail').querySelectorAll('button').forEach(x=>x.disabled=false); const assign=$('[data-action="assign"]'); if(assign && !$('#crew-choice')?.options.length) assign.disabled=true; }
  }));
  $('#case-detail').querySelectorAll('[data-action],[data-status],[data-draft]').forEach(button => {
    const permission = button.dataset.status ? 'work-status' : button.dataset.action === 'work-order-type' ? 'supervise' : 'dispatch';
    if(!hasPermission(permission) || ['approve','assign'].includes(button.dataset.action) && needsSupervisor && !hasPermission('supervise')) button.remove();
  });
  if(needsSupervisor && !hasPermission('supervise')) { const notice=document.createElement('p'); notice.className='hint'; notice.textContent='A supervisor must approve and assign this high-hazard response.'; $('#case-detail .detail-actions').append(notice); }
  $('#case-detail .routing-override summary')?.replaceChildren(document.createTextNode(staff('staff.dynamic.supervisor_routing_override_dabdb47c')));
  if(!hasPermission('dispatch')) $('#crew-choice')?.remove();
  if(!hasPermission('supervise')) $('#type-choice')?.closest('details')?.remove();
  loadCrewEligibility(incident);
  window.CivicOpsMap?.attachCase(incident);
}
function renderWorkerQualifications(worker) {
  return `<details class="worker-qualification"><summary>${esc(worker.displayName)} <span class="worker-meta">${staff('staff.dynamic.synthetic_worker_dec5bd12')}</span></summary><p class="worker-skills"><strong>${staff('staff.dynamic.skills_66d0f523')}</strong> · ${worker.skills.map(skill=>`${esc(skill.code)} · level ${skill.level}`).join('; ') || staff('staff.dynamic.no_skills_recorded_17814f39')}</p><div class="cert-list">${worker.certifications.map(cert=>{
    const expired = Date.parse(cert.expiresAt) <= Date.now();
    const future = Date.parse(cert.issuedAt) > Date.now();
    const status = expired ? 'expired' : future ? 'warned' : 'valid';
    return `<div class="certification"><div class="certification-head"><strong>${esc(cert.code)}</strong><span class="status-badge ${status}">${expired ? 'EXPIRED' : future ? staff('staff.dynamic.not_yet_valid_61159916') : 'VALID'}</span></div><p class="cert-meta">${expired ? staff('staff.dynamic.expired_424a2551') : staff('staff.dynamic.valid_until_92cd054d')} ${esc(date(cert.expiresAt))}${future ? ' · Starts ' + esc(date(cert.issuedAt)) : ''}</p><p class="cert-meta">${esc(cert.issuer)} · ${esc(cert.source)}<br>Evidence ${esc(cert.evidenceRef)}</p></div>`;
  }).join('') || `<p class="hint">${staff('staff.dynamic.no_certifications_recorded_6cbf5c2e')}</p>`}</div></details>`;
}
function renderCrewQualifications(item) {
  const status = ['eligible','warned','blocked'].includes(item.status) ? item.status : 'blocked';
  return `<article class="qualification"><div class="qualification-head"><strong>${esc(item.crew.name)}</strong><span class="status-badge ${status}">${esc(item.status.toUpperCase())}</span></div><ul class="eligibility-reasons">${item.reasons.map(reason=>`<li>${esc(reason)}</li>`).join('')}</ul>${item.qualifications.map(renderWorkerQualifications).join('')}</article>`;
}
async function loadCrewEligibility(incident) {
  if(!hasPermission('operations-read')) { $('#crew-eligibility').textContent = incident.assignedCrewName ? staff('staff.dynamic.assigned_crew_d619a302') + incident.assignedCrewName : staff('staff.dynamic.crew_qualification_review_is_available_to_municipal_ope_4282417c'); return; }
  try {
    const crews = await api(`/api/incidents/${encodeURIComponent(incident.id)}/crew-eligibility`,{},true);
    if(state.selected?.id !== incident.id || state.selected.version !== incident.version) return;
    $('#crew-eligibility').innerHTML = crews.filter(item=>item.crew.department===incident.department).map(renderCrewQualifications).join('') || `<p class="empty">${staff('staff.dynamic.no_crew_in_this_department_escalate_to_a_dispatcher_for_f4cb3acc')}</p>`;
    const selector=$('#crew-choice');
    if(selector) { const eligible=crews.filter(item=>item.canAssign); selector.innerHTML=eligible.map(item=>`<option value="${esc(item.crew.id)}">${esc(item.crew.name)} · ${esc(item.status)}</option>`).join(''); const assign=$('[data-action="assign"]'); if(assign) assign.disabled=!eligible.length; }
  } catch(error) { if(state.selected?.id === incident.id && $('#crew-eligibility')) $('#crew-eligibility').textContent=error.message; }
}
function renderOutbox(outbox) {
  $('#outbox-mode').textContent = outbox.status.mode;
  $('#outbox').innerHTML = outbox.items.length ? `<div class="table-wrap" tabindex="0" role="region" aria-label="${esc(staff('staff.outbox.scrollRegion'))}"><table><caption>${outbox.status.pending} pending · ${outbox.status.delivered} delivered · ${outbox.status.deadLetter} require attention</caption><thead><tr><th>${staff('staff.dynamic.case_15f86e24')}</th><th>${staff('staff.dynamic.operation_0f044feb')}</th><th>${staff('staff.dynamic.delivery_52bfe584')}</th><th>${staff('staff.dynamic.attempts_next_try_a55d445d')}</th></tr></thead><tbody>${outbox.items.slice(0,50).map(x=>`<tr><td>${esc(x.caseReference)}</td><td>${esc(x.operation)}</td><td>${esc(x.state)}${x.lastError ? `<small>${esc(x.lastError)}</small>` : ''}${x.state==='DeadLetter' ? `<button class="secondary" data-retry="${esc(x.id)}">${staff('staff.dynamic.retry_delivery_a9e1b443')}</button>` : ''}</td><td>${x.attempts} · ${esc(date(x.nextAttemptAt))}</td></tr>`).join('')}</tbody></table></div>` : `<p class="empty">${staff('staff.dynamic.delivery_records_appear_after_the_first_report_49b4b94f')}</p>`;
  if(!hasPermission('integration-retry')) $('#outbox').querySelectorAll('[data-retry]').forEach(button=>button.remove());
  $('#outbox').querySelectorAll('[data-retry]').forEach(button=>button.addEventListener('click',async()=>{button.disabled=true;try {await api('/api/integrations/outbox/'+encodeURIComponent(button.dataset.retry)+'/retry',{method:'POST'},true); await refresh();}catch(error){result('#control-error',error.message,false);}finally{button.disabled=false;}}));
}
const sosAlarm = { silenced: false, seen: new Set(), context: null };
function announceSos(alerts) {
  const banner = $('#sos-priority-banner');
  const open = alerts.filter(alert => (alert.status === 'Triggered' || alert.status === 'AwaitingLocation') && !alert.acknowledgedAt);
  const message = open.length ? `${open.length} unacknowledged SOS alert${open.length === 1 ? '' : 's'}. A named operator must acknowledge.` : '';
  if (banner) {
    banner.hidden = open.length === 0;
    banner.textContent = message;
  }
  const live = $('#sos-live-banner');
  if (live) {
    live.hidden = open.length === 0;
    live.textContent = message;
  }
  if (!open.length || sosAlarm.silenced || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  const fresh = open.filter(alert => !sosAlarm.seen.has(alert.id));
  open.forEach(alert => sosAlarm.seen.add(alert.id));
  if (!fresh.length) return;
  try {
    const AudioContext = window.AudioContext || window.webkitAudioContext;
    if (!AudioContext) return;
    sosAlarm.context ||= new AudioContext();
    const audio = sosAlarm.context;
    const start = audio.currentTime;
    const oscillator = audio.createOscillator();
    const gain = audio.createGain();
    oscillator.frequency.value = 880;
    gain.gain.setValueAtTime(0.05, start);
    gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.18);
    oscillator.connect(gain);
    gain.connect(audio.destination);
    oscillator.start(start);
    oscillator.stop(start + 0.18);
  } catch { /* Autoplay or a missing audio device must not hide the visual alarm. */ }
}
function renderSosQueue(queue) {
  const target = $('#sos-queue');
  if (!target) return;
  const alerts = Array.isArray(queue?.alerts) ? queue.alerts : [];
  $('#sos-queue-count').textContent = String(alerts.length);
  announceSos(alerts);
  if (!alerts.length) {
    target.innerHTML = '<p class="empty">No active SOS alerts. The queue is monitored by authorised municipal staff.</p>';
    $('#sos-alert-card').hidden = true;
    state.selectedSos = null;
    return;
  }
  target.innerHTML = alerts.map(alert => `<button class="sos-queue-item ${alert.status === 'Triggered' || alert.status === 'AwaitingLocation' ? 'priority' : ''}" data-sos-id="${esc(alert.id)}" type="button"><span><strong>${alert.status === 'AwaitingLocation' ? 'Location needed' : 'SOS alert'}</strong><small>${esc(alert.channel)} · ${esc(date(alert.triggeredAt))}</small></span><span class="pill warned">${esc(alert.status)}</span></button>`).join('');
  target.querySelectorAll('[data-sos-id]').forEach(button => button.addEventListener('click', () => {
    state.selectedSos = alerts.find(alert => alert.id === button.dataset.sosId) || null;
    renderSosDetail(state.selectedSos);
  }));
  state.selectedSos = alerts.find(alert => alert.id === state.selectedSos?.id) || alerts[0];
  renderSosDetail(state.selectedSos);
}
function renderSosDetail(alert) {
  const card = $('#sos-alert-card');
  if (!card) return;
  if (!alert) { card.hidden = true; return; }
  card.hidden = false;
  $('#sos-alert-heading').textContent = `${alert.status === 'AwaitingLocation' ? 'SOS awaiting location' : 'SOS alert'} · ${alert.id}`;
  $('#sos-alert-status').textContent = alert.status;
  const location = alert.location ? `${Number(alert.location.latitude).toFixed(5)}, ${Number(alert.location.longitude).toFixed(5)}` : 'Not shared';
  const who = alert.staffName ? `${alert.staffName}${alert.crewName ? ` · ${alert.crewName}` : ''}` : `Resident · ${alert.contactHash || 'anonymous hash'}`;
  const pin = alert.location
    ? `<div class="sos-map-pin" aria-label="SOS coordinate card"><span aria-hidden="true"></span><small>${esc(location)}</small><p>Inline coordinate card. No live map tiles are loaded.</p></div>`
    : '<div class="sos-map-pin"><p>No coordinates yet. Use the address or landmark if one was shared.</p></div>';
  $('#sos-alert-summary').innerHTML = `<div><dt>Who</dt><dd>${esc(who)}</dd></div><div><dt>Channel</dt><dd>${esc(alert.channel)}</dd></div><div><dt>Triggered</dt><dd>${esc(date(alert.triggeredAt))}</dd></div><div><dt>Location</dt><dd>${esc(location)}</dd></div><div><dt>Accuracy</dt><dd>${alert.accuracyMeters == null ? 'Unavailable' : `${esc(alert.accuracyMeters)} m`}</dd></div><div><dt>Battery</dt><dd>${alert.batteryPercent == null ? 'Unavailable' : `${esc(alert.batteryPercent)}%`}</dd></div><div><dt>Escalation</dt><dd>${esc(alert.escalationLevel || 'None')}</dd></div>`;
  $('#sos-alert-context').innerHTML = `${pin}<p id="sos-nearest" class="hint">Checking straight-line crew positions…</p>${alert.silent ? '<p class="hint">Silent on the worker device. The control room alarm still sounds.</p>' : ''}${alert.repeatedFalseAlarmReview ? '<p class="hint">Repeated false alarm flagged for human review only. No score, penalty or restriction was applied.</p>' : ''}`;
  $('#sos-timeline-items').innerHTML = (alert.audit || []).map(item => `<li><strong>${esc(item.action)}</strong><span>${esc(item.actor)} · ${esc(date(item.at))}</span>${item.note ? `<small>${esc(item.note)}</small>` : ''}</li>`).join('') || '<li>No audit entries.</li>';
  const suggestToken = ++sosSuggestToken;
  suggestNearestCrew(alert, suggestToken);
  const actions = {Triggered:'acknowledge',AwaitingLocation:'acknowledge',Acknowledged:'assess',Dispatched:'resolve'};
  const action = actions[alert.status];
  const dispatch = alert.status === 'Assessing' && hasPermission('dispatch')
    ? `<button class="primary" data-sos-action="dispatch" data-sos-note="Dispatch nearest municipal crew. Straight-line suggestion only; a named human chose the crew. CivicOps did not place an emergency call." type="button">Dispatch nearest municipal crew</button><button class="secondary" data-sos-action="dispatch" data-sos-note="Hand over to SAPS/EMS via 10111/10177. CivicOps did not place the call. The operator will use the municipal procedure." type="button">Hand over to SAPS/EMS via 10111/10177</button>`
    : '';
  const primary = action && hasPermission('dispatch') ? `<button class="primary" data-sos-action="${action}" type="button">${action[0].toUpperCase()+action.slice(1)} SOS</button>` : '';
  const falseAlarm = hasPermission('dispatch') && (action || dispatch) ? '<button class="secondary" data-sos-action="false-alarm" data-sos-note="Synthetic operator marked a false alarm. The user is notified by a simulated record only. No penalty was applied." type="button">Mark false alarm</button>' : '';
  $('#sos-alert-actions').innerHTML = (primary || dispatch || falseAlarm) ? `${primary}${dispatch}${falseAlarm}` : '<p class="hint">A named authorised human must record the next decision. Every action is audited.</p>';
  $('#sos-alert-actions').querySelectorAll('[data-sos-action]').forEach(button => button.addEventListener('click', async () => {
    button.disabled = true;
    const note = button.dataset.sosNote || `Synthetic authorised human recorded ${button.dataset.sosAction}.`;
    try { await api(`/api/v1/sos/${encodeURIComponent(alert.id)}/${button.dataset.sosAction}`, {method:'POST',body:JSON.stringify({operationId:crypto.randomUUID(),note})}, true); await refresh(); }
    catch (error) { result('#control-error', error.message, false); button.disabled = false; }
  }));
}
let sosSuggestToken = 0;
function sosDistanceKm(lat1, lon1, lat2, lon2) {
  const radians = Math.PI / 180;
  const arc = Math.sin((lat2 - lat1) * radians / 2) ** 2 + Math.cos(lat1 * radians) * Math.cos(lat2 * radians) * Math.sin((lon2 - lon1) * radians / 2) ** 2;
  return 2 * 6371 * Math.asin(Math.min(1, Math.sqrt(arc)));
}
async function suggestNearestCrew(alert, token) {
  const target = $('#sos-nearest');
  if (!target) return;
  if (!alert?.location || !hasPermission('operations-read')) { target.textContent = 'No SOS coordinates are available for a straight-line crew suggestion.'; return; }
  try {
    const layers = await api('/api/v1/map/layers?layers=crews', {}, true);
    if (token !== sosSuggestToken) return;
    let best = null;
    for (const feature of layers.features || []) {
      const coordinates = feature.geometry?.coordinates;
      if (!Array.isArray(coordinates) || coordinates.length < 2) continue;
      const kilometres = sosDistanceKm(alert.location.latitude, alert.location.longitude, coordinates[1], coordinates[0]);
      const name = feature.properties?.name || feature.properties?.crewName || 'Municipal crew';
      if (!best || kilometres < best.kilometres) best = { name, kilometres };
    }
    target.textContent = best
      ? `Straight-line suggestion only: ${best.name} is about ${best.kilometres.toFixed(1)} km away. A named human chooses. This is not a dispatch and not a route time.`
      : 'No crew position is available for a straight-line suggestion. A named human can still dispatch or hand over.';
  } catch { if (token === sosSuggestToken) target.textContent = 'Crew positions could not be loaded. A named human can still dispatch or hand over.'; }
}
document.getElementById('sos-silence')?.addEventListener('click', () => { sosAlarm.silenced = true; });
async function refresh() {
  if(state.refreshing) return; state.refreshing = true;
  const authEpoch = state.authEpoch;
  try {
    const operations = hasPermission('operations-read');
    const [identity,cases,crews,ai,outbox,alerts,metrics,catalogue,sosQueue] = await Promise.all([api('/api/dispatcher',{},true),api('/api/incidents',{},true),api('/api/crews'),api('/api/ai/status'),operations ? api('/api/integrations/outbox',{},true) : null,operations ? api('/api/channels/elevenlabs/alerts',{},true) : [],operations ? api('/api/metrics',{},true) : null,api('/api/work-order-types',{},true),operations ? api('/api/v1/sos/queue',{},true) : null]);
    if(authEpoch !== state.authEpoch) return;
    state.cases = cases; state.crews = crews; state.ai = ai; state.metrics = metrics; state.catalogue = catalogue.workOrderTypes; state.sosQueue = sosQueue; state.lastRefresh = Date.now();
    $('#identity').textContent = identity.identity; $('#control-error').hidden = true; $('#last-refresh').textContent = staff('staff.dynamic.updated_a919c7c2') + date(state.lastRefresh);
    $('#channel-alerts').hidden = !alerts.length;
    $('#alert-items').innerHTML = alerts.slice().reverse().slice(0,20).map(x=>`<article class="timeline-item"><h3>${esc(x.referenceNumber || staff('staff.dynamic.unlinked_conversation_a09baa89'))}</h3><p>${esc(x.alert)}</p><span class="hint">${esc(date(x.at))}</span></article>`).join('');
    renderKpis(); renderQueue(); if(outbox) renderOutbox(outbox); if(sosQueue) renderSosQueue(sosQueue); if(state.selected) renderDetail(cases.find(x=>x.id===state.selected.id)); network();
  } catch(error) { if(authEpoch === state.authEpoch) { result('#control-error',error.message,false); network(true); } }
  finally { state.refreshing=false; }
}
async function loadAudit() {
  const authEpoch = state.authEpoch;
  try { const [verification,events] = await Promise.all([api('/api/audit/verify',{},true),api('/api/audit',{},true)]); if(authEpoch !== state.authEpoch) return; result('#audit-result',`${verification.valid ? 'VERIFIED' : staff('staff.dynamic.integrity_failure_823a4cdb')} · ${verification.entries} events. ${verification.message}`,verification.valid); $('#audit-rows').innerHTML = events.slice().reverse().map(x=>`<tr><td>${x.sequence}</td><td>${esc(x.referenceNumber)}</td><td>${esc(displayName(x.action))}<small>${esc(displayName(x.actor))}</small></td><td>${esc(date(x.at))}</td><td><code title="${esc(x.hash)}">${esc(x.hash.slice(0,16))}…</code></td></tr>`).join(''); }
  catch(error) { if(authEpoch === state.authEpoch) result('#audit-result',error.message,false); }
}
$('#verify-audit').addEventListener('click',loadAudit);
$('#export-evidence').addEventListener('click',async event=>{
  const button=event.currentTarget; if(button.disabled) return; button.disabled=true;
  try { const evidence=await api('/api/evidence/export',{},true); const url=URL.createObjectURL(new Blob([JSON.stringify(evidence,null,2)],{type:'application/json'})); const link=document.createElement('a'); link.href=url; link.download='civicops-evidence-'+new Date().toISOString().slice(0,10)+'.json'; document.body.append(link); link.click(); link.remove(); setTimeout(()=>URL.revokeObjectURL(url),1000); }
  catch(error) { result('#audit-result',error.message,false); }
  finally { button.disabled=false; }
});
async function startStream() {
  state.streamAbort?.abort(); state.streamAbort = new AbortController();
  try {
    const headers = state.token ? {Authorization:`Bearer ${state.token}`} : {};
    const response = await fetch(localPath('/api/stream'),{headers,signal:state.streamAbort.signal,cache:'no-store',credentials:'same-origin'});
    if(!response.ok || !response.body) return;
    const reader = response.body.getReader(); const decoder = new TextDecoder(); let pending='';
    while(true) { const {value,done} = await reader.read(); if(done) break; pending += decoder.decode(value,{stream:true}); let index; while((index=pending.indexOf('\n\n'))>=0) { const event=pending.slice(0,index); pending=pending.slice(index+2); if(['refresh','training.completed','certification.granted','sos.'].some(type=>event.includes('event: '+type))) { if(state.view==='control') await refresh(); if(state.view==='audit') await loadAudit(); } } if(pending.length>4096) pending=''; }
  } catch { /* Polling remains active if SSE disconnects. */ }
}
let privateConversation = null;
let clientLoad = null;
function loadElevenLabsClient() {
  if (window.ElevenLabsClient?.Conversation) return Promise.resolve(window.ElevenLabsClient.Conversation);
  if (!clientLoad) clientLoad = new Promise((resolve,reject) => {
    const script = document.createElement('script');
    script.nonce = pageScriptNonce;
    script.src = 'https://cdn.jsdelivr.net/npm/@elevenlabs/client@1.27.0/dist/lib.iife.js';
    script.integrity = 'sha384-ekuWfdL0BkeVAWv24yeCWtzVwYDQd4pCkPL5TKVDLmwDBeVxS2cTwo0wfsUNaFkJ';
    script.crossOrigin = 'anonymous';
    script.onload = () => window.ElevenLabsClient?.Conversation ? resolve(window.ElevenLabsClient.Conversation) : reject(new Error(t('ui.clientFailed')));
    script.onerror = () => { clientLoad = null; reject(new Error(t('ui.clientRetry'))); };
    document.head.append(script);
  });
  return clientLoad;
}
function prepareWidgetStyles(widget) {
  // Only this trusted widget's shadow-root style insertions receive the page nonce.
  // Its pinned release generates theme CSS dynamically and does not expose a nonce option.
  const prepare = root => {
    const allowStyles = node => {
      if(node.nodeType === Node.ELEMENT_NODE && node.tagName === 'STYLE') node.nonce = pageScriptNonce;
      if(node.nodeType === Node.DOCUMENT_FRAGMENT_NODE) node.querySelectorAll('style').forEach(style => { style.nonce = pageScriptNonce; });
    };
    for(const method of ['appendChild','insertBefore','replaceChild']) {
      const insert = root[method].bind(root);
      root[method] = (node,...arguments_) => { allowStyles(node); return insert(node,...arguments_); };
    }
    return root;
  };
  const attach = widget.attachShadow.bind(widget);
  widget.attachShadow = options => prepare(attach(options));
  if(widget.shadowRoot) prepare(widget.shadowRoot);
}
function privateControls(active) {
  $('#private-voice').disabled = active; $('#private-text').disabled = active;
  $('#private-stop').disabled = !active; $('#private-message').disabled = !active; $('#private-send').disabled = !active;
}
const SPEND_CAP_LABEL='Spend cap reached. Simulation mode';
function spendCapBanner(){ let el=document.getElementById('spend-cap-banner'); if(!el){ el=document.createElement('div'); el.id='spend-cap-banner'; el.setAttribute('role','status'); el.className='spend-cap-banner'; el.textContent=SPEND_CAP_LABEL; document.body.prepend(el); } el.hidden=false; }
function spendCapSimulated(){ spendCapBanner(); $('#live-agent').hidden=true; $('#private-agent').hidden=true; $('#simulated-agent').hidden=false; $('#agent-mode').textContent=SPEND_CAP_LABEL; }
async function startPrivate(mode) {
  if(i18n.language === 'zu') { $('#private-status').textContent=t('ui.zuluVoiceUnavailable'); return; }
  privateControls(true);
  try {
    const Client = await loadElevenLabsClient();
    const session = await api('/api/channels/elevenlabs/session');
    if (session.reason === 'voice_cap_reached') { privateControls(false); spendCapSimulated(); return; }
    if (session.mode !== 'live' || !(session.signed_url || session.token)) throw new Error(t('ui.privateUnavailable'));
    const credentials = session.token ? {conversationToken:session.token,connectionType:'webrtc'} : {signedUrl:session.signed_url,connectionType:'websocket'};
    privateConversation = await Client.startSession({...credentials, textOnly:mode === 'text', overrides:{agent:{language:i18n.language}},
      onMessage: message => { const element=document.createElement('p'); element.className='bubble '+(message.source === 'user' ? 'user' : 'agent'); element.textContent=String(message.message || '').slice(0,4000); $('#private-log').append(element); element.scrollIntoView({block:'nearest'}); },
      onStatusChange: ({status}) => { $('#private-status').textContent=t(status==='connecting' ? 'ui.sessionConnecting' : status==='disconnected' ? 'ui.sessionDisconnected' : 'ui.connectedSession',{channel:'voice/text'}); if(status==='disconnected') { privateConversation=null; privateControls(false); } },
      onError: () => { $('#private-status').textContent=t('ui.sessionInterrupted'); },
      onDisconnect: details => { privateConversation=null; privateControls(false); if(/quota|credit/i.test(String(details?.message || details?.reason || ''))) spendCapSimulated(); }
    });
    $('#private-status').textContent=t('ui.connectedSession',{channel:mode});
  } catch(error) { privateConversation=null; privateControls(false); $('#private-status').textContent=error.message; }
}
$('#private-voice').addEventListener('click',()=>startPrivate('voice'));
$('#private-text').addEventListener('click',()=>startPrivate('text'));
$('#private-stop').addEventListener('click',async()=>{ try { await privateConversation?.endSession(); } finally { privateConversation=null; privateControls(false); $('#private-status').textContent=t('ui.sessionEnded'); } });
$('#private-chat-form').addEventListener('submit',event=>{event.preventDefault(); const message=$('#private-message').value.trim(); if(message && privateConversation) { privateConversation.sendUserMessage(message); $('#private-message').value=''; } });
window.addEventListener('pagehide',()=>privateConversation?.endSession());
async function initialize() {
  await i18n.ready;
  try {
    const [health,channels] = await Promise.all([api('/healthz'),api('/api/channels/status'),loadIdentity()]);
    const channel = channels.elevenlabs;
    state.health=health; state.channels=channels; $('#mode').textContent=health.demoMode ? 'EXPLICIT DEMO' : t('ui.humanApproval'); $('#seed').hidden=!health.demoMode; $('#agent-mode').textContent=channel.mode==='simulated' ? 'SIMULATED' : 'ELEVENLABS LIVE';
    await refreshReadiness();
    try { const caps = await api('/api/spend-caps'); if(caps.reached) spendCapBanner(); if(caps.voiceReached && channel.mode==='live') { spendCapSimulated(); channel.mode='simulated'; } } catch { /* status is advisory */ }
    if(channel.mode==='live') {
      $('#simulated-agent').hidden=true;
      if(channel.sessionMode==='public') {
        const session = await api('/api/channels/elevenlabs/session');
        if(session.reason === 'voice_cap_reached') { spendCapSimulated(); }
        else {
        if(!session.agentId) throw new Error(t('ui.agentNotConfigured'));
        window.__webpack_nonce__ = pageScriptNonce;
        window.__fpjs_d_m = true; // Disable the widget dependency's optional third-party monitoring request.
        $('#live-agent').hidden=false; const widget=document.createElement('elevenlabs-convai'); prepareWidgetStyles(widget); widget.setAttribute('agent-id',session.agentId); widget.setAttribute('language',i18n.language); widget.setAttribute('server-location',session.serverLocation || 'global'); $('#live-agent').append(widget);
        const script=document.createElement('script'); script.nonce=pageScriptNonce; script.src='https://unpkg.com/@elevenlabs/convai-widget-embed@0.19.0/dist/index.js'; script.integrity='sha384-Hq6Ry47vrwXsbkoZrxzEYn2bHG8p+KtLso8+Tlz2FjHptRWI0FTgAKQoh25jF4Ms'; script.crossOrigin='anonymous'; script.async=true; script.onerror=()=>{ $('#agent-mode').textContent=t('ui.widgetUnavailable'); $('#private-agent-note').hidden=false; $('#private-agent-note').textContent=t('ui.widgetFailure'); }; document.head.append(script);
        }
      } else {
        $('#private-agent').hidden=false;
        $('#private-text').hidden=channel.sessionMode==='webrtc';
      }
    }
    await updateResidentLanguage(); network(); setView(location.hash.slice(1)); if(hasPermission('operations-read')) startStream();
  } catch(error) {
    network(true); $('#agent-mode').textContent=t('ui.agentUnavailableLabel'); $('#private-agent-note').hidden=false; $('#private-agent-note').textContent=error.message;
    // Explicit demo: if the live resident agent cannot start, fall back to the labelled simulated agent.
    if(state.health?.demoMode && state.channels?.elevenlabs?.mode === 'live') { $('#live-agent').hidden=true; $('#private-agent').hidden=true; $('#simulated-agent').hidden=false; $('#agent-mode').textContent='SIMULATED (live voice unavailable)'; }
  }
}
async function refreshReadiness() {
  const banner = $('#degraded-service');
  try {
    const readiness = await api('/health/ready');
    const degraded = readiness.status === 'degraded';
    banner.hidden = !degraded;
    if (degraded) {
      const messages = [];
      if (readiness.providers.ai.status === 'degraded') messages.push(t('ui.degradedAi'));
      if (readiness.providers.elevenlabs.status === 'degraded') messages.push(t('ui.degradedVoice'));
      if (readiness.providers.zendesk.status === 'degraded') messages.push(t('ui.degradedTicket'));
      banner.textContent = t(state.health?.demoMode ? 'ui.degradedDemo' : 'ui.degraded') + messages.join('; ') + '.';
    }
  } catch {
    banner.hidden = false;
    banner.textContent = t('ui.readinessUnknown');
  }
}
setInterval(()=>{if(state.view==='control') refresh(); refreshReadiness(); if(state.lastRefresh && Date.now()-state.lastRefresh>45000) network(true);},15000);
if('serviceWorker' in navigator) navigator.serviceWorker.register('/service-worker.js').catch(()=>{});
window.addEventListener('DOMContentLoaded',initialize,{once:true});

async function updateResidentLanguage() {
  const selected = i18n.language;
  for(const id of ['chat-log','private-status','network','degraded-service']) $('#' + id).lang=selected;
  $('#chat-log .bubble.agent[data-i18n]')?.setAttribute('lang',selected);
  const live = state.channels?.elevenlabs?.mode === 'live';
  if(live) {
    $('#live-agent').hidden = selected === 'zu' || state.channels.elevenlabs.sessionMode !== 'public';
    $('#private-agent').hidden = selected === 'zu' || state.channels.elevenlabs.sessionMode === 'public';
    $('#private-agent-note').hidden = selected !== 'zu';
    if(selected === 'zu') $('#private-agent-note').textContent=t('ui.zuluVoiceUnavailable');
    $('#live-agent elevenlabs-convai')?.setAttribute('language',selected);
  }
  try {
    const notice = await api('/api/privacy/notice?language=' + selected);
    if(selected !== i18n.language) return;
    $('#privacy-purpose').textContent=notice.text;
    $('#privacy-officer').textContent=notice.informationOfficerContact ? t('ui.privacyOfficer',{contact:notice.informationOfficerContact}) : t('ui.privacyOfficerPending');
  } catch { /* Packaged translated purpose stays available without the service. */ }
}
window.addEventListener('civicops:language',() => { updateResidentLanguage(); network(); refreshReadiness(); });
