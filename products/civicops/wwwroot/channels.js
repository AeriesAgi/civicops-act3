'use strict';
// Purpose-limited staff inbox. No contact revelation, media fetch or automated dispatch.
window.CivicOpsChannels = (function () {
  const q = selector => document.querySelector(selector);
  const model = {conversations:[],templates:[],selected:null,detail:null,readiness:null,metrics:[],pendingReply:null,sending:false,loading:false,generation:0,selection:0,templatesGeneration:0};
  const labels = {sms:'SMS',whatsapp:'WhatsApp',voice:'Voice',web:'Web'};
  const channel = value => Object.hasOwn(labels,value) ? value : 'web';
  const icons = {
    sms:'<path d="M4 4h16v12H9l-5 4V4Z"/><path d="M8 8h8M8 12h5"/>',
    whatsapp:'<path d="M20 11a8 8 0 0 1-11 7.4L4 20l1.5-5A8 8 0 1 1 20 11Z"/><path d="M8 8c1 4 3 6 7 7l1-2-3-1-1 1-2-2 1-1-1-3-2 1Z"/>',
    voice:'<path d="m7 3 3 5-2 2c1 3 3 5 6 6l2-2 5 3c0 3-2 4-4 4C9 20 4 15 3 7c0-2 1-4 4-4Z"/>',
    web:'<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c5 5 5 13 0 18-5-5-5-13 0-18Z"/>'
  };
  const icon = value => `<span class="channel-icon channel-${channel(value)}"><svg width="22" height="22" viewBox="0 0 24 24" aria-hidden="true" focusable="false">${icons[channel(value)]}</svg></span>`;
  const shorten = (value,length=78) => String(value || '').length > length ? String(value).slice(0,length-1)+'…' : String(value || '');
  const age = at => { const minutes=Math.max(0,Math.floor((Date.now()-Date.parse(at))/60000)); return Number.isFinite(minutes) ? minutes < 1 ? 'Just now' : minutes < 60 ? minutes+' min ago' : date(at) : 'Time unavailable'; };
  const showError = (selector,message) => {q(selector).textContent=message || '';q(selector).hidden=!message;};
  function reset() {
    model.generation++;model.selection++;model.templatesGeneration++;model.loading=false;model.sending=false;
    model.conversations=[];model.templates=[];model.selected=null;model.detail=null;model.readiness=null;model.metrics=[];model.pendingReply=null;
    for(const selector of ['#inbox-list','#inbox-metrics','#channel-readiness','#channel-metrics','#channel-template-list'])q(selector)?.replaceChildren();
    q('#inbox-workspace').innerHTML='<div class="inbox-empty"><span class="eyebrow">RESIDENT CONVERSATIONS</span><h2>Choose a conversation.</h2><p>Sign in to review purpose-limited messages within your municipality.</p></div>';
    q('#inbox-workspace').removeAttribute('aria-busy');
    q('#inbox-content').hidden=true;q('#inbox-locked').hidden=false;q('#integrations-content').hidden=true;q('#integrations-locked').hidden=false;
    q('#channel-template-text').value='';q('#channel-template-reason').value='';q('#channel-template-preview').textContent='Choose a template to preview.';
    q('#channel-template-key').replaceChildren();q('#channel-template-version').textContent='Versioned';
    for(const selector of ['#inbox-error','#integrations-error','#channel-template-result'])showError(selector,'');
  }
  function denied(error,epoch) {
    if(epoch!==state.authEpoch)return true;
    if(![401,403].includes(error?.status))return false;
    clearStaffData();state.auth=null;state.token='';loadIdentity().catch(()=>{});return true;
  }
  function renderMetrics() {
    q('#inbox-metrics').innerHTML=['voice','sms','whatsapp'].map(name=>{
      const metric=model.metrics.find(item=>item.channel===name) || {};
      return `<article class="kpi channel-kpi">${icon(name)}<span class="label">${labels[name]} inbound</span><strong>${Number(metric.inbound)||0}</strong><small>${Number(metric.casesCreated)||0} cases · ${Number(metric.humanQueue)||0} in human queue</small></article>`;
    }).join('') + kpi('Human call-taker queue',model.conversations.filter(item=>item.humanQueue || item.callbackRequested).length,'Queue requests; a human decides the next action');
    q('#channel-metrics').innerHTML=model.metrics.length ? model.metrics.map(item=>`<tr><th scope="row">${esc(labels[channel(item.channel)])}</th><td>${Number(item.inbound)||0}</td><td>${Number(item.casesCreated)||0}</td><td>${Number(item.outbound)||0}</td><td>${Number(item.suppressed)||0}</td><td>${Number(item.humanQueue)||0}</td></tr>`).join('') : '<tr><td colspan="6">No retained channel activity yet.</td></tr>';
  }
  function renderReadiness() {
    const data=model.readiness;
    q('#channel-external-mode').textContent=data?.demoMode ? 'EXPLICIT DEMO · SIMULATED' : data?.externalSendsEnabled ? 'Human approval required' : 'External delivery disabled';
    q('#channel-readiness').innerHTML=(data?.channels || []).map(item=>`<article class="card channel-provider"><div class="card-heading"><div class="provider-heading">${icon(item.channel)}<div><span class="eyebrow">${esc(labels[channel(item.channel)])}</span><h2>${esc(item.provider)}</h2></div></div><span class="pill ${item.mode==='disabled' ? 'muted' : item.mode==='configured' ? 'eligible' : 'warned'}">${esc(String(item.mode).toUpperCase())}</span></div><p>${esc(item.detail)}</p><p class="hint">${item.mode==='configured' ? 'Configuration is present. Live account, provider retention, capacity and municipal acceptance require owner validation.' : item.mode==='simulated' ? 'Synthetic local receipts and outboxes. This mode records no live delivery.' : 'This channel is unavailable until the owner completes its configuration.'}</p></article>`).join('');
  }
  function renderList() {
    const selectedChannel=q('#inbox-channel-filter').value;
    const queueOnly=q('#inbox-queue-filter').checked;
    const items=model.conversations.filter(item=>(!selectedChannel || item.channel===selectedChannel) && (!queueOnly || item.humanQueue || item.callbackRequested)).sort((a,b)=>Date.parse(b.updatedAt)-Date.parse(a.updatedAt));
    q('#inbox-count').textContent=items.length+' conversations';
    q('#inbox-list').innerHTML=items.length ? items.map(item=>`<button type="button" class="inbox-conversation${item.id===model.selected ? ' selected' : ''}" data-conversation="${esc(item.id)}" aria-pressed="${item.id===model.selected}"><span class="conversation-heading">${icon(item.channel)}<span><strong>${esc(item.residentLabel)}</strong><small>${esc(labels[channel(item.channel)])} · ${esc(item.language)} · ${esc(item.provider)}</small></span></span><span class="conversation-subject">${esc(item.caseReferences?.[0] || (item.callbackRequested ? 'Callback requested' : 'Resident service conversation'))}</span><span class="conversation-footer"><span class="pill ${item.suppressed ? 'blocked' : item.humanQueue || item.callbackRequested ? 'warned' : 'muted'}">${item.suppressed ? 'OPTED OUT' : item.humanQueue || item.callbackRequested ? 'HUMAN QUEUE' : 'HUMAN REVIEW'}</span><time datetime="${esc(item.updatedAt)}">${esc(age(item.updatedAt))}</time></span></button>`).join('') : '<p class="empty">No conversations match these filters. Use the labelled synthetic channel exercise in demo mode.</p>';
    q('#inbox-list').querySelectorAll('[data-conversation]').forEach(button=>button.addEventListener('click',()=>select(button.dataset.conversation,true)));
  }
  const variables = detail => ({
    reference:detail?.caseReferences?.[0] || 'CIV-20261009-00000011',
    update:q('#inbox-reply-update')?.value.trim() || 'A municipal dispatcher is reviewing this report. No arrival time is promised.',
    request:q('#inbox-reply-request')?.value.trim() || 'Please describe the service issue from a safe distance. Do not send identity documents.'
  });
  const interpolate = (text,values) => String(text || '').replace(/\{([a-zA-Z]+)\}/g,(match,key)=>Object.hasOwn(values,key)?values[key]:match);
  function currentReplyTemplate() {
    return model.templates.find(item=>item.key===q('#inbox-reply-template')?.value && item.language===q('#inbox-reply-language')?.value);
  }
  function updateReplyPreview() {
    const template=currentReplyTemplate();
    const preview=q('#inbox-reply-preview');if(!preview)return;
    preview.textContent=template ? interpolate(template.text,variables(model.detail)) : 'No template exists for this language.';
    preview.lang=q('#inbox-reply-language').value;
    const optedOut=model.detail?.suppressed;
    const notice=template?.key==='channel.notice';
    const sinceResident=Date.now()-Date.parse(model.detail?.lastResidentMessageAt);
    const interactive=notice && model.detail?.channel==='whatsapp' && !template.providerTemplateName && sinceResident>=0 && sinceResident<24*60*60*1000;
    const menu=q('#inbox-interactive-preview');menu.hidden=!interactive;
    const menuLabels=template?.language==='af' ? ["Meld 'n probleem",'Volg my saak','SOS-inligting'] : template?.language==='zu' ? ['Bika inkinga','Hlola icala lami','Ulwazi lwe-SOS'] : ['Report a problem','Check my case','SOS info'];
    menu.innerHTML=interactive ? `<span class="eyebrow">WHATSAPP MENU PREVIEW · OPEN 24-HOUR WINDOW</span><ul class="channel-menu-preview">${menuLabels.map(label=>`<li lang="${esc(template.language)}">${esc(label)}</li>`).join('')}</ul>` : '';
    const allowed=!model.sending && hasPermission('dispatch') && model.readiness?.demoMode===true && !optedOut && (model.detail?.consentAccepted || notice) && !!template;
    q('#inbox-send').disabled=!allowed || !q('#inbox-human-approve').checked || q('#inbox-reply-reason').value.trim().length<10;
    q('#inbox-reply-version').textContent=template ? `v${template.version} · ${template.reviewState || 'Owner review pending'}` : 'Template unavailable';
    q('#inbox-send-state').textContent=optedOut ? 'Resident opted out on this channel. Outbound is blocked until they send START.' : !model.detail?.consentAccepted && !notice ? 'Awaiting the resident’s first-contact purpose consent. A human may approve the channel.notice template; other replies are blocked.' : !hasPermission('dispatch') ? 'Your role can inspect this conversation. A dispatcher must approve the reply.' : !model.readiness?.demoMode ? 'External sending is disabled. Municipal/provider acceptance remains owner-pending.' : 'Your approval records a simulated outbox message and your server-verified identity.';
  }
  function renderConversation(detail) {
    model.detail=detail;
    const refs=detail.caseReferences || [];
    const messages=detail.messages || [];
    q('#inbox-workspace').innerHTML=`<div class="conversation-detail-heading"><div class="conversation-title">${icon(detail.channel)}<div><span class="eyebrow">${esc(labels[channel(detail.channel)])} · ${esc(detail.provider)}</span><h2>${esc(detail.residentLabel)}</h2><p class="hint">Contact masked · ${esc(detail.language)} · last resident contact ${esc(date(detail.lastResidentMessageAt || detail.updatedAt))}</p></div></div><span class="pill ${detail.suppressed ? 'blocked' : 'warned'}">${detail.suppressed ? 'OPTED OUT' : 'HUMAN DECISIONS'}</span></div><div class="conversation-case-strip"><div><span class="eyebrow">SAME MUNICIPAL CASE PIPELINE</span><p>${refs.length ? refs.map(reference=>`<button type="button" class="case-link" data-channel-case="${esc(reference)}">${esc(reference)} <span aria-hidden="true">↗</span></button>`).join(' ') : 'No case linked. Commands and privacy choices are retained in this conversation.'}</p></div>${detail.humanQueue || detail.callbackRequested ? `<div class="call-taker-notice"><strong>${detail.callbackRequested ? 'Callback requested' : 'Human call-taker requested'}</strong><span>A named staff member reviews the queue. No call or dispatch occurs automatically.</span></div>` : ''}</div><div class="conversation-timeline" id="inbox-timeline" role="region" aria-label="Conversation timeline" tabindex="0">${messages.length ? messages.map(message=>{
      const inbound=message.direction==='inbound';const outbound=message.direction==='outbound';
      return `<article class="channel-message ${inbound ? 'inbound' : outbound ? 'outbound' : 'system'}"><div class="message-label"><strong>${inbound ? 'Resident' : outbound ? 'Human-approved reply' : 'Service information / draft'}</strong>${message.simulated ? '<span class="pill muted">SIMULATED</span>' : ''}</div><p lang="${esc(detail.language)}">${esc(message.text)}</p>${message.mediaReferences?.length ? `<div class="channel-media-refs"><span class="eyebrow">MEDIA REFERENCES · NOT FETCHED</span><ul>${message.mediaReferences.map(reference=>`<li><code>${esc(reference)}</code></li>`).join('')}</ul><p class="hint">Provider IDs only. This inbox opens no files, thumbnails, audio or frames.</p></div>` : ''}<div class="message-metadata"><time datetime="${esc(message.at)}">${esc(date(message.at))}</time>${message.templateKey ? `<span>${esc(message.templateKey)} · v${Number(message.templateVersion)||1}</span>` : ''}${message.caseReference ? `<span>${esc(message.caseReference)}</span>` : ''}${message.actor ? `<span>Reviewed by ${esc(message.actor)}</span>` : ''}${message.deliveryState ? `<span>${esc(message.deliveryState)}</span>` : ''}</div></article>`;
    }).join('') : '<p class="empty">No retained messages in this conversation.</p>'}</div><form id="inbox-reply-form" class="inbox-reply"><div class="card-heading"><div><span class="eyebrow">HUMAN APPROVAL · VERSIONED TEMPLATE</span><h3>Review a resident reply</h3></div><span class="pill warned">${model.readiness?.demoMode ? 'SIMULATED OUTBOX' : 'EXTERNAL DELIVERY DISABLED'}</span></div><div class="form-row"><div><label for="inbox-reply-template">Quick reply template</label><select id="inbox-reply-template">${[...new Set(model.templates.map(item=>item.key))].map(key=>`<option value="${esc(key)}">${esc(key.replace('case.','').replaceAll('-',' '))}</option>`).join('')}</select></div><div><label for="inbox-reply-language">Reply language</label><select id="inbox-reply-language"><option value="en">English</option><option value="af">Afrikaans</option><option value="zu">isiZulu</option></select></div></div><div class="form-row"><div><label for="inbox-reply-update">Public update variable</label><input id="inbox-reply-update" maxlength="600" value="A municipal dispatcher is reviewing this report. No arrival time is promised."/></div><div><label for="inbox-reply-request">Request information variable</label><input id="inbox-reply-request" maxlength="600" value="Please describe the service issue from a safe distance."/></div></div><div class="reply-preview"><span class="eyebrow">REPLY PREVIEW</span><p id="inbox-reply-preview"></p><small id="inbox-reply-version"></small><div id="inbox-interactive-preview" hidden></div></div><label for="inbox-reply-reason">Reason for this reply</label><input id="inbox-reply-reason" maxlength="500" minlength="10" required placeholder="Explain the reviewed case context and purpose"/><label class="check"><input type="checkbox" id="inbox-human-approve"/><span>I reviewed the case, privacy choice and language. I approve this simulated reply as ${esc(state.auth?.name || 'the signed-in dispatcher')}.</span></label><p class="hint" id="inbox-send-state"></p><button type="submit" class="primary" id="inbox-send" disabled>${model.readiness?.demoMode ? 'Approve simulated reply' : 'Outbound disabled'}</button><div id="inbox-reply-result" role="status" aria-live="polite"></div></form>`;
    q('#inbox-reply-language').value=['en','af','zu'].includes(detail.language)?detail.language:'en';
    for(const selector of ['#inbox-reply-template','#inbox-reply-language','#inbox-human-approve'])q(selector).addEventListener('change',updateReplyPreview);
    for(const selector of ['#inbox-reply-update','#inbox-reply-request','#inbox-reply-reason'])q(selector).addEventListener('input',updateReplyPreview);
    q('#inbox-reply-form').addEventListener('submit',sendReply);
    q('#inbox-workspace').querySelectorAll('[data-channel-case]').forEach(button=>button.addEventListener('click',async()=>{
      const epoch=state.authEpoch;try {const incident=await api('/api/dispatch/incidents/'+encodeURIComponent(button.dataset.channelCase),{},true);if(epoch!==state.authEpoch)return;location.hash='control';setView('control');renderDetailCase(incident);}
      catch(error){if(!denied(error,epoch))showError('#inbox-error',error.message);}
    }));
    updateReplyPreview();
  }
  // Keep the incident workspace's global renderer separate from the channel renderer.
  function renderDetailCase(incident) { window.CivicOpsChannels.openCase(incident); }
  async function select(id,focus=false,refresh=false) {
    if(!refresh && model.detail?.id===id){if(focus)q('#inbox-workspace').focus({preventScroll:true});return;}
    const draft=model.detail?.id===id && q('#inbox-reply-form') ? {template:q('#inbox-reply-template').value,language:q('#inbox-reply-language').value,update:q('#inbox-reply-update').value,request:q('#inbox-reply-request').value,reason:q('#inbox-reply-reason').value} : null;
    const epoch=state.authEpoch;const selection=++model.selection;model.selected=id;model.detail=null;renderList();
    q('#inbox-workspace').setAttribute('aria-busy','true');
    q('#inbox-workspace').innerHTML='<div class="inbox-empty" role="status"><span class="eyebrow">PURPOSE-LIMITED STAFF INBOX</span><h2>Loading conversation…</h2><p>Replies become available after the current conversation is verified.</p></div>';
    try { const detail=await api('/api/v1/channels/conversations/'+encodeURIComponent(id),{},true);if(epoch!==state.authEpoch || selection!==model.selection)return;renderConversation(detail);if(draft){for(const [key,selector] of Object.entries({template:'#inbox-reply-template',language:'#inbox-reply-language',update:'#inbox-reply-update',request:'#inbox-reply-request',reason:'#inbox-reply-reason'}))q(selector).value=draft[key];updateReplyPreview();result('#inbox-reply-result','Conversation changed. Your draft is retained; review the current timeline before approving it again.',false);}if(focus){q('#inbox-workspace').focus({preventScroll:true});} }
    catch(error){if(!denied(error,epoch) && selection===model.selection)showError('#inbox-error',error.message);}
    finally{if(epoch===state.authEpoch && selection===model.selection)q('#inbox-workspace').removeAttribute('aria-busy');}
  }
  async function sendReply(event) {
    event.preventDefault();const button=q('#inbox-send');if(button.disabled || model.sending)return;
    const template=currentReplyTemplate();if(!template || !model.detail || !q('#inbox-human-approve').checked)return;
    const epoch=state.authEpoch;const id=model.detail.id;model.sending=true;button.disabled=true;
    const output=q('#inbox-reply-result');output.className='hint';output.hidden=false;output.textContent='Recording the approved simulated reply…';
    const body={templateKey:template.key,language:template.language,variables:variables(model.detail),expectedTemplateVersion:template.version,expectedConversationVersion:model.detail.version,reason:q('#inbox-reply-reason').value.trim()};
    const fingerprint=JSON.stringify({id,...body,expectedConversationVersion:undefined});
    if(model.pendingReply?.fingerprint!==fingerprint)model.pendingReply={fingerprint,body:{...body,operationId:crypto.randomUUID()}};
    Object.assign(body,model.pendingReply.body);
    try {const response=await api('/api/v1/channels/conversations/'+encodeURIComponent(id)+'/reply',{method:'POST',body:JSON.stringify(body)},true);if(epoch!==state.authEpoch)return;model.sending=false;if(model.pendingReply?.body.operationId===body.operationId)model.pendingReply=null;await reload();if(epoch!==state.authEpoch || model.selected!==id)return;result('#inbox-reply-result',response.mode==='simulated' ? 'SIMULATED · Human-approved reply recorded in the local outbox. No live message was sent.' : 'Reply intent recorded for operator review.');}
    catch(error){if(!denied(error,epoch)){model.sending=false;if(model.selected===id){if(error.status===409)model.pendingReply=null;if(q('#inbox-reply-result'))result('#inbox-reply-result',error.message,false);updateReplyPreview();}}}
    finally{if(epoch===state.authEpoch){model.sending=false;updateReplyPreview();}}
  }
  async function reload() {
    if(!hasPermission('operations-read')){q('#inbox-content').hidden=true;q('#inbox-locked').hidden=false;q('#integrations-content').hidden=true;q('#integrations-locked').hidden=false;return;}
    if(model.loading || model.sending)return;model.loading=true;const epoch=state.authEpoch;const generation=++model.generation;const previousTemplate=currentReplyTemplate();
    q('#inbox-refresh').disabled=true;q('#integrations-refresh').disabled=true;
    try {
      const [conversations,templates,readiness,metrics]=await Promise.all([api('/api/v1/channels/conversations',{},true),api('/api/v1/channels/templates',{},true),api('/api/v1/channels/readiness',{},true),api('/api/v1/channels/metrics',{},true)]);
      if(epoch!==state.authEpoch || generation!==model.generation)return;
      model.conversations=conversations;model.templates=templates;model.readiness=readiness;model.metrics=metrics;
      q('#inbox-content').hidden=false;q('#inbox-locked').hidden=true;q('#integrations-content').hidden=false;q('#integrations-locked').hidden=true;
      q('#inbox-seed').hidden=!readiness.demoMode || !hasPermission('dispatch');
      q('#inbox-data-label').textContent=readiness.demoMode ? 'SYNTHETIC · SIMULATED' : 'Municipal channel records';
      renderMetrics();renderReadiness();renderList();showError('#inbox-error','');showError('#integrations-error','');
      q('#inbox-last-refresh').textContent='Updated '+date(new Date());
      if(model.selected && conversations.some(item=>item.id===model.selected)){if(conversations.find(item=>item.id===model.selected).version!==model.detail?.version)await select(model.selected,false,true);}
      else if(conversations.length)await select(conversations.toSorted((a,b)=>Date.parse(b.updatedAt)-Date.parse(a.updatedAt))[0].id);
      if(q('#inbox-reply-form') && model.detail?.id===model.selected){if(previousTemplate && currentReplyTemplate()?.version!==previousTemplate.version){q('#inbox-human-approve').checked=false;result('#inbox-reply-result','Template changed. Your draft is retained; review the current preview before approving it again.',false);}updateReplyPreview();}
    } catch(error) {if(!denied(error,epoch)){showError('#inbox-error',error.message);showError('#integrations-error',error.message);}}
    finally {if(generation===model.generation){model.loading=false;q('#inbox-refresh').disabled=false;q('#integrations-refresh').disabled=false;}}
  }
  function editorTemplate() {return model.templates.find(item=>item.key===q('#channel-template-key').value && item.language===q('#channel-template-language').value);}
  function templateDraftPreview() {
    q('#channel-template-preview').textContent=interpolate(q('#channel-template-text').value,{reference:'CIV-20261009-00000011',update:'Awaiting human review. No arrival time is promised.',request:'Please describe the issue from a safe distance.'});
    q('#channel-template-preview').lang=q('#channel-template-language').value;
    q('#channel-template-preview-state').textContent='Unsaved text preview · synthetic variables · save validates the template';
  }
  function populateEditor() {
    const template=editorTemplate();q('#channel-template-text').value=template?.text || '';q('#channel-template-provider-name').value=template?.providerTemplateName || '';
    q('#channel-template-version').textContent=template ? 'v'+template.version : 'Unavailable';
    q('#channel-template-review').textContent=template?.reviewState || 'Native-speaker and municipal review pending.';
    q('#channel-template-text').lang=q('#channel-template-language').value;templateDraftPreview();
  }
  function renderTemplateList() {
    q('#channel-template-list').innerHTML=[...new Set(model.templates.map(item=>item.key))].map(key=>`<article class="template-record"><strong>${esc(key)}</strong><div class="template-language-versions">${model.templates.filter(item=>item.key===key).map(item=>`<button class="template-language" type="button" data-template-key="${esc(key)}" data-template-language="${esc(item.language)}">${esc(item.language.toUpperCase())} <span>v${item.version}</span></button>`).join('')}</div></article>`).join('');
    q('#channel-template-list').querySelectorAll('[data-template-key]').forEach(button=>button.addEventListener('click',()=>{q('#channel-template-key').value=button.dataset.templateKey;q('#channel-template-language').value=button.dataset.templateLanguage;populateEditor();}));
  }
  async function loadTemplates() {
    if(!hasPermission('admin'))return;
    const epoch=state.authEpoch;const generation=++model.templatesGeneration;
    try {
      const templates=await api('/api/v1/channels/templates',{},true);if(epoch!==state.authEpoch || generation!==model.templatesGeneration)return;
      const previous=q('#channel-template-key').value;model.templates=templates;renderTemplateList();
      q('#channel-template-key').innerHTML=[...new Set(templates.map(item=>item.key))].map(key=>`<option value="${esc(key)}">${esc(key)}</option>`).join('');
      if(templates.some(item=>item.key===previous))q('#channel-template-key').value=previous;
      populateEditor();showError('#channel-template-result','');
    } catch(error) {if(!denied(error,epoch))result('#channel-template-result',error.message,false);}
  }
  q('#inbox-refresh').addEventListener('click',reload);q('#integrations-refresh').addEventListener('click',reload);
  q('#inbox-channel-filter').addEventListener('change',renderList);q('#inbox-queue-filter').addEventListener('change',renderList);
  for(const selector of ['#inbox-open-access','#integrations-open-access'])q(selector).addEventListener('click',()=>{q('#staff-access').open=true;q('#staff-access').scrollIntoView({block:'start'});q('#staff-access summary').focus();});
  q('#inbox-seed').addEventListener('click',async event=>{
    const button=event.currentTarget;if(button.disabled)return;button.disabled=true;const epoch=state.authEpoch;
    try {await api('/api/v1/channels/demo/seed',{method:'POST',body:'{}'},true);if(epoch===state.authEpoch)await reload();}
    catch(error){if(!denied(error,epoch))showError('#inbox-error',error.message);}
    finally{button.disabled=false;}
  });
  for(const selector of ['#channel-template-key','#channel-template-language'])q(selector).addEventListener('change',populateEditor);
  q('#channel-template-text').addEventListener('input',templateDraftPreview);
  q('#channel-template-form').addEventListener('submit',async event=>{
    event.preventDefault();const button=q('#channel-template-save');if(button.disabled)return;const template=editorTemplate();if(!template)return;
    const epoch=state.authEpoch;button.disabled=true;
    try {const saved=await api('/api/admin/channel-templates/'+encodeURIComponent(template.key)+'/'+encodeURIComponent(template.language),{method:'PUT',body:JSON.stringify({text:q('#channel-template-text').value,providerTemplateName:q('#channel-template-provider-name').value.trim() || null,expectedVersion:template.version,reason:q('#channel-template-reason').value.trim()})},true);if(epoch!==state.authEpoch)return;await loadTemplates();if(epoch!==state.authEpoch)return;result('#channel-template-result',`Template ${saved.key} · ${saved.language} saved as v${saved.version} and audited.`);}
    catch(error){if(!denied(error,epoch))result('#channel-template-result',error.message,false);}
    finally{button.disabled=false;}
  });
  q('#channel-template-server-preview').addEventListener('click',async event=>{
    const button=event.currentTarget;if(button.disabled)return;const template=editorTemplate();if(!template)return;const epoch=state.authEpoch;button.disabled=true;
    try {const preview=await api('/api/admin/channel-templates/preview',{method:'POST',body:JSON.stringify({key:template.key,language:template.language,variables:{reference:'CIV-20261009-00000011',update:'Awaiting human review. No arrival time is promised.',request:'Please describe the issue from a safe distance.'}})},true);if(epoch!==state.authEpoch)return;q('#channel-template-preview').textContent=preview.text;q('#channel-template-preview-state').textContent='Saved server template v'+preview.version+' · '+preview.reviewState;}
    catch(error){if(!denied(error,epoch))result('#channel-template-result',error.message,false);}
    finally{button.disabled=false;}
  });
  setInterval(()=>{if(['inbox','integrations'].includes(state.view))reload();},15000);
  window.addEventListener('offline',()=>{if(['inbox','integrations'].includes(state.view))showError('#inbox-error','Offline · messages remain on the server. Reconnect and refresh before approving a reply.');});
  window.addEventListener('pagehide',reset);
  return {show:reload,reset,loadTemplates,openCase:incident=>renderDetail(incident),diagnostics:()=>({selected:model.selected,detailId:model.detail?.id,conversations:model.conversations.length,loading:model.loading,generation:model.generation})};
})();
