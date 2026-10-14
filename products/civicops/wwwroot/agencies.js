'use strict';
// Multi-agency board and simulated scene-event feed. Humans decide every command, mutual-aid and detection outcome.
window.CivicOpsAgencies = (function () {
  const q = selector => document.querySelector(selector);
  const model = {agencies:[], board:null, feed:null, selected:null, busy:false};
  const op = prefix => prefix + '-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2,8);
  const agency = id => model.agencies.find(a => a.id === id) || {id, name:id, colour:'#526977'};
  const chip = id => { const a = agency(id); return `<span class="agency-chip" data-colour="${esc(a.colour)}">${esc(a.name)}${a.restricted ? ' 🔒' : ''}</span>`; };
  const post = (path, body) => api(path, {method:'POST', body:JSON.stringify(body)}, true);
  const error = message => { q('#agency-error').textContent = message || ''; q('#agency-error').hidden = !message; };
  const statusPill = s => `<span class="pill ${s === 'Closed' ? 'muted' : s === 'Open' ? 'warned' : ''}">${esc(s)}</span>`;

  function kpis() {
    const b = model.board || {open:0, active:0, pendingMutualAid:0};
    const proposed = (model.feed?.events || []).filter(e => e.status === 'Proposed').length;
    q('#agency-kpis').innerHTML = [['Open incidents', b.open, 'Awaiting an incident commander'], ['Active incidents', b.active, 'Commander assigned'],
      ['Mutual aid pending', b.pendingMutualAid, 'A supervisor decides each request'], ['Camera events to review', proposed, 'Simulated scene events only']]
      .map(([label, value, note]) => `<article class="kpi"><span class="label">${label}</span><strong>${Number(value) || 0}</strong><small>${note}</small></article>`).join('');
  }
  function board() {
    const items = model.board?.incidents || [];
    q('#agency-count').textContent = items.length + (items.length === 1 ? ' incident' : ' incidents');
    q('#agency-list').innerHTML = items.length ? items.map(i => `<button type="button" class="agency-item${model.selected === i.id ? ' selected' : ''}" data-incident="${esc(i.id)}">
      <span class="agency-item-head"><strong>${esc(i.title)}</strong>${statusPill(i.status)}</span>
      <span class="agency-chips">${i.agencies.map(chip).join('')}</span>
      <small>${esc(i.typeCode)} · ${esc(i.priority)} priority${i.commander ? ' · IC ' + esc(i.commander) : ' · no commander yet'}${i.redacted ? ' · restricted details hidden' : ''}</small></button>`).join('')
      : '<div class="empty-state"><h3>No cross-agency incidents yet.</h3><p class="hint">Load the synthetic scenario, or confirm a camera event to open one.</p></div>';
  }
  function detail() {
    const i = (model.board?.incidents || []).find(x => x.id === model.selected);
    const host = q('#agency-detail');
    if (!i) { host.innerHTML = '<span class="eyebrow">INCIDENT WORKSPACE</span><h2>Choose an incident</h2><p class="hint">See every agency, the incident commander, mutual aid and the shared timeline.</p>'; return; }
    const supervise = hasPermission('supervise'), dispatch = hasPermission('dispatch'), open = i.status !== 'Closed';
    const participating = i.agencies.map(a => `<option value="${esc(a)}">${esc(agency(a).name)}</option>`).join('');
    const all = model.agencies.filter(a => a.enabled).map(a => `<option value="${esc(a.id)}">${esc(a.name)}</option>`).join('');
    host.innerHTML = `<div class="card-heading"><div><span class="eyebrow">INCIDENT ${esc(i.id)} · SYNTHETIC</span><h2>${esc(i.title)}</h2></div>${statusPill(i.status)}</div>
      <p>${esc(i.summary)}</p><div class="agency-chips">${i.agencies.map(chip).join('')}</div>
      <dl class="agency-facts"><dt>Incident commander</dt><dd>${i.commander ? esc(i.commander) + ' · ' + esc(i.commanderIcsRole) : 'Not assigned'}</dd><dt>Priority</dt><dd>${esc(i.priority)}</dd>
      <dt>Location</dt><dd>${i.addressOrLandmark ? esc(i.addressOrLandmark) : i.location ? esc(i.location.latitude.toFixed(4) + ', ' + i.location.longitude.toFixed(4)) : i.redacted ? 'Restricted' : 'Not supplied'}</dd>${i.detectionId ? `<dt>Source</dt><dd>Human-confirmed camera event ${esc(i.detectionId)}</dd>` : ''}</dl>
      ${supervise && open ? `<details class="agency-more"${i.commander ? '' : ' open'}><summary>${i.commander ? 'Reassign incident commander' : 'Assign incident commander'}</summary><form class="agency-form" id="agency-commander-form"><label for="agency-commander">Name</label><input id="agency-commander" maxlength="120" required value="Synthetic Station Officer A">
        <label for="agency-ics">ICS role</label><select id="agency-ics"><option>Incident Commander</option><option>Operations Section Chief</option><option>Liaison Officer</option><option>Safety Officer</option></select><button class="primary" type="submit">Assign commander</button></form></details>` : ''}
      <h3 class="case-section-heading">Mutual aid</h3>${i.mutualAid.length ? i.mutualAid.map(m => `<div class="aid-row"><div><strong>${esc(agency(m.fromAgency).name)} → ${m.toTenantId ? esc(m.toTenantId) + ' · ' : ''}${esc(agency(m.toAgency).name)}</strong><small>${esc(m.resources)} · share ${esc(m.scope)} · requested by ${esc(m.requestedBy)}</small>${m.decidedBy ? `<small>${esc(m.status)} by ${esc(m.decidedBy)}: ${esc(m.decisionNote || '')}</small>` : ''}</div>
        ${m.status === 'Requested' && supervise && open ? `<span class="aid-actions"><button class="primary" data-aid="${esc(m.id)}" data-accept="true" type="button">Accept</button><button class="secondary" data-aid="${esc(m.id)}" data-accept="false" type="button">Decline</button></span>` : `<span class="pill ${m.status === 'Requested' ? 'warned' : 'muted'}">${esc(m.status)}</span>`}</div>`).join('') : '<p class="hint">No mutual-aid requests.</p>'}
      ${dispatch && open ? `<details class="agency-more"><summary>Request mutual aid</summary><form class="agency-form" id="agency-aid-form"><label for="aid-from">From agency</label><select id="aid-from">${participating}</select><label for="aid-to">To agency</label><select id="aid-to">${all}</select>
        <label for="aid-tenant">Neighbouring municipality id (optional)</label><input id="aid-tenant" maxlength="64" placeholder="neighbour-metro"><label for="aid-resources">Resources</label><input id="aid-resources" maxlength="300" required value="One rescue pumper and crew">
        <label for="aid-scope">Share</label><select id="aid-scope"><option value="Summary">Summary only</option><option value="SummaryAndLocation">Summary and location</option></select><button class="secondary" type="submit">Send request for a human decision</button></form></details>
        <div class="agency-status-actions">${['Active','Contained','Closed'].filter(s => s !== i.status).map(s => `<button class="secondary" data-agency-status="${s}" type="button">Mark ${s.toLowerCase()}</button>`).join('')}</div>` : ''}
      <h3 class="case-section-heading">Shared timeline</h3><ol class="agency-timeline">${i.timeline.map(t => `<li><strong>${esc(t.action.replaceAll('-', ' '))}</strong> <span>${esc(t.actor)}</span> <time>${esc(date(t.at))}</time>${t.note ? `<small>${esc(t.note.replace(/^operation:\S+\s?/, ''))}</small>` : ''}</li>`).join('')}</ol>`;
  }
  function feed() {
    const events = model.feed?.events || [];
    q('#camera-notice').textContent = model.feed?.notice || 'SIMULATED camera events. Scene events only.';
    q('#camera-list').innerHTML = events.length ? events.slice(0, 12).map(e => `<div class="camera-row camera-${esc(e.status.toLowerCase())}"><div><strong>${esc(e.eventType.replaceAll('_', ' '))}</strong> <span class="pill muted">${Math.round(e.confidence * 100)}% confidence</span>${e.lowConfidence ? ' <span class="pill warned">Low confidence</span>' : ''}
      <small>${esc(e.cameraId)} · ${esc(date(e.observedAt))} · suggests ${e.suggestedAgencies.map(a => esc(agency(a).name)).join(', ')}</small>${e.decidedBy ? `<small>${esc(e.status)} by ${esc(e.decidedBy)}</small>` : ''}</div>
      ${e.status === 'Proposed' && hasPermission('dispatch') ? `<span class="aid-actions"><button class="primary" data-detection="${esc(e.id)}" data-confirm="true" type="button">Confirm</button><button class="secondary" data-detection="${esc(e.id)}" data-confirm="false" type="button">Dismiss</button></span>` : `<span class="pill ${e.status === 'Confirmed' ? '' : 'muted'}">${esc(e.status)}</span>`}</div>`).join('')
      : '<div class="empty-state"><h3>No camera events.</h3><p class="hint">Run the simulated feed. No real camera is connected.</p></div>';
  }
  function paint() { document.querySelectorAll('#agencies [data-colour]').forEach(el => { if (/^#[0-9a-f]{6}$/i.test(el.dataset.colour)) el.style.setProperty('--agency', el.dataset.colour); }); }
  function render() { kpis(); board(); detail(); feed(); paint(); q('#agency-seed').hidden = !hasPermission('dispatch'); q('#camera-simulate').hidden = !hasPermission('dispatch'); }
  async function load() {
    if (!hasPermission('operations-read')) { q('#agency-content').hidden = true; q('#agency-locked').hidden = false; return; }
    q('#agency-content').hidden = false; q('#agency-locked').hidden = true; q('#agency-seed').hidden = !hasPermission('dispatch'); q('#camera-simulate').hidden = !hasPermission('dispatch');
    try {
      const [agencies, boardData, feedData] = await Promise.all([api('/api/v1/agencies', {}, true), api('/api/v1/agency-incidents', {}, true), api('/api/v1/detections', {}, true)]);
      model.agencies = agencies; model.board = boardData; model.feed = feedData;
      if (!model.selected && boardData.incidents.length) model.selected = boardData.incidents[0].id;
      error(''); render();
    } catch (e) { error(e.message || 'Could not load the agency board.'); }
  }
  async function act(work) { if (model.busy) return; model.busy = true; try { await work(); await load(); } catch (e) { error(e.message || 'Action failed.'); } finally { model.busy = false; } }
  function bind() {
    q('#agency-list').addEventListener('click', e => { const b = e.target.closest('[data-incident]'); if (b) { model.selected = b.dataset.incident; board(); detail(); paint(); } });
    q('#agency-seed').addEventListener('click', () => act(async () => {
      const i = await post('/api/v1/agency-incidents', {operationId:op('demo-crash'), title:'Crash with downed streetlight pole, Synthetic Main Rd', typeCode:'TRAFFIC.CRASH_SCENE', agencies:['ems','fire','electricity'],
        summary:'Synthetic two-vehicle crash. One occupant trapped; streetlight pole down across the left lane.', location:{longitude:31.021, latitude:-29.858}, addressOrLandmark:'Synthetic Main Rd and 3rd Ave'});
      await post('/api/v1/agency-incidents/' + i.id + '/mutual-aid', {operationId:op('demo-aid'), fromAgency:'fire', toAgency:'fire', toTenantId:'neighbour-metro', resources:'One heavy rescue unit', scope:'Summary', note:'Synthetic extrication support request.'});
      await post('/api/v1/agency-incidents', {operationId:op('demo-flood'), title:'Low-water bridge flooding, Synthetic Ward 4', typeCode:'DISASTER.FLOOD_EVACUATION', agencies:['roads','ems'],
        summary:'Synthetic rising water at a low-water bridge; four households to move to the community hall.', location:{longitude:30.985, latitude:-29.915}, addressOrLandmark:'Synthetic Ward 4 bridge'});
      await post('/api/v1/detections/simulate', {operationId:op('demo-sim'), seed:13, count:6});
      model.selected = i.id;
    }));
    q('#camera-simulate').addEventListener('click', () => act(() => post('/api/v1/detections/simulate', {operationId:op('sim'), seed:Math.floor(Math.random() * 1000), count:4})));
    q('#agency-detail').addEventListener('submit', e => {
      e.preventDefault(); const id = model.selected;
      if (e.target.id === 'agency-commander-form') act(() => post(`/api/v1/agency-incidents/${id}/commander`, {operationId:op('cmd'), commander:q('#agency-commander').value, icsRole:q('#agency-ics').value, note:'Assigned from the agency board.'}));
      if (e.target.id === 'agency-aid-form') act(() => post(`/api/v1/agency-incidents/${id}/mutual-aid`, {operationId:op('aid'), fromAgency:q('#aid-from').value, toAgency:q('#aid-to').value,
        toTenantId:q('#aid-tenant').value.trim() || null, resources:q('#aid-resources').value, scope:q('#aid-scope').value, note:'Requested from the agency board.'}));
    });
    q('#agency-detail').addEventListener('click', e => {
      const id = model.selected, aid = e.target.closest('[data-aid]'), status = e.target.closest('[data-agency-status]');
      if (aid) act(() => post(`/api/v1/agency-incidents/${id}/mutual-aid/${aid.dataset.aid}/decision`, {operationId:op('dec'), accept:aid.dataset.accept === 'true', note:aid.dataset.accept === 'true' ? 'Accepted by supervisor.' : 'Declined by supervisor.'}));
      if (status) act(() => post(`/api/v1/agency-incidents/${id}/status`, {operationId:op('st'), status:status.dataset.agencyStatus, note:'Status set from the agency board.'}));
    });
    q('#camera-list').addEventListener('click', e => {
      const b = e.target.closest('[data-detection]'); if (!b) return;
      act(async () => { const r = await post(`/api/v1/detections/${b.dataset.detection}/${b.dataset.confirm === 'true' ? 'confirm' : 'dismiss'}`, {operationId:op('det'), note:b.dataset.confirm === 'true' ? 'Operator verified the simulated scene event.' : 'Operator dismissed as a false positive.'}); if (r.incidentId) model.selected = r.incidentId; });
    });
  }
  document.addEventListener('DOMContentLoaded', bind);
  setInterval(() => { if (state.view === 'agencies' && !model.busy) load(); }, 15000);
  return {show: load};
})();
