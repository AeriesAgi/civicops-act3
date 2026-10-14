'use strict';
(() => {
  const $ = selector => document.querySelector(selector);
  const escape = value => String(value ?? '').replace(/[&<>"']/g,char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
  const date = value => new Date(value).toLocaleString('en-ZA',{dateStyle:'medium',timeStyle:'short'});
  const {CrewDatabase,LocationGate,retryDelay,JOB_TTL,DRAFT_TTL} = window.CivicCrewCore;
  const database = new CrewDatabase(); const gate = new LocationGate();
  const municipality = location.pathname.match(/^\/m\/[^/]+/)?.[0] || '';
  const apiPath = path => municipality + path;
  const app = {auth:null,me:null,token:'',paired:null,epoch:0,partition:null,jobs:[],jobsFreshAt:0,outbox:[],selected:null,screen:'signin',offlineProfile:false,live:false,syncing:false,watch:null,locationEpoch:0,lastPing:0,strings:{},language:'en',shiftKey:null,photo:null,photoUrl:null,photoEpoch:0,install:null,refreshing:false};
  function message(text,bad=false) { const target=$('#message');target.textContent=text;target.classList.toggle('error',bad);target.hidden=false; }
  const requests=new Set();
  const identity=()=>Object.freeze({epoch:app.epoch,partition:app.partition,token:app.token,csrfToken:app.auth?.csrfToken});
  const current=context=>context.epoch===app.epoch&&context.partition===app.partition;
  function requireCurrent(context) { if(!current(context)) { const error=new Error('Identity changed; stale work ignored.');error.stale=true;throw error; } }
  function invalidateIdentity(clearCredentials=true) {
    app.epoch++;for(const controller of requests)controller.abort();requests.clear();
    stopSharing('Identity changed; sharing stopped.');clearPhoto();
    if(clearCredentials) { app.token='';app.paired=null;sessionStorage.removeItem('civicops-crew-paired-session'); }
    app.partition=null;app.me=null;app.jobs=[];app.outbox=[];app.selected=null;app.shiftKey=null;app.live=false;app.offlineProfile=false;app.refreshing=false;app.syncing=false;
    $('#job-list').replaceChildren();$('#job-detail').replaceChildren();$('#location-history').replaceChildren();$('#outbox-list').replaceChildren();$('#outbox-count').textContent='0';show('signin',false);updateIndicators();return identity();
  }
  async function beginIdentity() { const context=invalidateIdentity();await database.clear(()=>current(context));requireCurrent(context);return context; }
  async function api(path,options={},context=identity()) {
    requireCurrent(context);
    if (!navigator.onLine) { const error=new Error('Offline. Work drafts stay on this device; sharing has stopped.');error.offline=true;error.epoch=context.epoch;throw error; }
    const headers=new Headers(options.headers || {});if(options.body)headers.set('Content-Type','application/json');
    if(context.token && (path.startsWith('/api/v1/crew/') || path.startsWith('/api/v1/sos')) && !path.endsWith('/pairing/exchange'))headers.set('Authorization','Bearer '+context.token);
    if(options.method && !['GET','HEAD','OPTIONS'].includes(options.method) && context.csrfToken && !context.token)headers.set('X-CivicOps-CSRF',context.csrfToken);
    const controller=new AbortController();requests.add(controller);const timer=setTimeout(()=>controller.abort(),20000);
    let response,data={};
    try { response=await fetch(apiPath(path),{...options,headers,signal:controller.signal,credentials:'same-origin',cache:'no-store'});try { data=await response.json(); }catch { requireCurrent(context); } }
    catch(error) { requireCurrent(context);if(error.stale)throw error;const failure=new Error('The server could not acknowledge this request. Job drafts remain queued; location is never queued.');failure.offline=true;failure.epoch=context.epoch;throw failure; }
    finally { clearTimeout(timer);requests.delete(controller); }
    requireCurrent(context);
    if(!response.ok) {
      const error=new Error(data.error || data.detail || data.message || 'Request failed ('+response.status+').');error.status=response.status;error.epoch=context.epoch;
      if([401,403].includes(response.status)&&path.startsWith('/api/v1/crew/')&&!path.endsWith('/pairing/exchange')&&!(path==='/api/v1/crew/locations'&&response.status===403)) {
        await clearPrivate('Crew access was lost. Collection stopped and private data cleared; sign in again.',context);error.stale=true;
      }
      throw error;
    }
    return data;
  }
  const activeShift = () => !!app.me?.shift && !app.me.shift.endedAt && Date.parse(app.me.shift.expiresAt)>Date.now();
  const activeConsent = () => !!app.me?.consent && !app.me.consent.withdrawnAt;
  const privacyPending = () => app.outbox.some(item=>item.kind==='privacy');
  const text = key => app.strings[app.language]?.[key] || app.strings.en?.[key] || key;
  const statusText = status => ({'en-route':text('enRoute'),'on-site':text('onSite'),'paused':text('paused'),'complete':text('complete'),'cannot-complete':text('cannotComplete')})[status] || status || 'Assigned';
  function translate() { document.documentElement.lang=app.language;document.querySelectorAll('[data-copy]').forEach(element=>{element.textContent=text(element.dataset.copy);});renderJobs();if(app.selected)renderDetail(); }
  function show(screen,focus=true) {
    if(!app.partition && !['signin','sos','privacy'].includes(screen))screen='signin';
    if(screen==='detail' && !app.selected)screen='jobs';
    app.screen=screen;document.querySelectorAll('.screen').forEach(element=>{element.hidden=element.id!==screen+'-view';});
    document.querySelectorAll('.bottom-nav [data-screen]').forEach(button=>{if(button.dataset.screen===screen || screen==='detail'&&button.dataset.screen==='jobs')button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');});
    if(focus) { $('#crew-main').focus({preventScroll:true});scrollTo({top:0,behavior:'instant'}); }
  }
  function projected(job) {
    const current=structuredClone(job);
    for(const item of app.outbox.filter(item=>item.jobId===job.id)) { if(item.action==='status')current.crewStatus=item.body.status;current.version++;current.pending=true; }
    return current;
  }
  function renderJobs() {
    if(!app.partition)return;
    const jobs=app.jobs.map(projected).filter(job=>!$('#job-filter').value || job.crewStatus===$('#job-filter').value);
    $('#crew-label').textContent='ASSIGNED TO '+String(app.me?.crewId || '').toUpperCase();
    $('#job-count').textContent=jobs.length+' assigned job'+(jobs.length===1?'':'s');
    $('#job-list').innerHTML=jobs.length ? jobs.map(job=>`<button class="job-card" type="button" data-job="${escape(job.id)}"><span class="job-top"><span class="reference">${escape(job.referenceNumber)}</span><span class="pill ${escape(job.priority?.toLowerCase())}">${escape(job.priority)} PRIORITY</span></span><h2>${escape(job.title)}</h2><span class="hint">${escape(job.department)}</span><span class="job-meta"><strong>${escape(statusText(job.crewStatus))}${job.pending?' · draft':''}</strong><span>${escape(job.hazardClass)} hazard <span class="arrow" aria-hidden="true">↗</span></span></span></button>`).join(''):'<p class="empty">No assigned jobs match this filter. A supervisor approves and assigns work; this app does not dispatch.</p>';
    $('#job-list').querySelectorAll('[data-job]').forEach(button=>button.addEventListener('click',()=>{app.selected=button.dataset.job;renderDetail();show('detail');}));
    $('#cache-note').textContent=app.offlineProfile?'Offline profile · cached jobs may be stale. Location and shift starts are unavailable.':'Jobs updated '+(app.jobsFreshAt?date(app.jobsFreshAt):'on sign-in')+'. Cached for up to 24 hours. '+(app.jobs.some(job=>job.synthetic)?'Synthetic work only.':'');
  }
  function mapSnippet(job) {
    if(!job.location)return '<p class="hint">No verified coordinate is available. Confirm the site with your supervisor; no position has been invented.</p>';
    const {longitude,latitude}=job.location;
    return `<div class="coordinate-map"><svg viewBox="0 0 320 150" role="img" aria-label="Coordinate snippet for unverified job location; this is not navigation"><path d="M0 35H320M0 75H320M0 115H320M40 0V150M100 0V150M160 0V150M220 0V150M280 0V150" stroke="#263d4d" stroke-width="1" fill="none"/><circle cx="160" cy="75" r="37" fill="#1c4445" stroke="#568079" stroke-dasharray="4 5"/><circle cx="160" cy="75" r="10" fill="#d8f58f"/><circle cx="160" cy="75" r="3" fill="#183335"/><path d="M160 16V32M160 118V134M101 75H117M203 75H219" stroke="#95c2c1" stroke-width="2"/></svg><p><strong>${Number(latitude).toFixed(5)}, ${Number(longitude).toFixed(5)}</strong><br>Coordinate view · unverified · no route guidance or tiles</p></div>`;
  }
  function renderDetail() {
    clearPhoto();const base=app.jobs.find(job=>job.id===app.selected);if(!base) { app.selected=null;show('jobs',false);return; }
    const job=projected(base);const blocked=app.outbox.some(item=>item.jobId===job.id && item.state!=='pending');
    const draftNotes=app.outbox.filter(item=>item.jobId===job.id && item.action==='notes');
    $('#job-detail').innerHTML=`<div class="detail-head"><span class="reference">${escape(job.referenceNumber)} · REVISION ${base.version}</span><h1 id="detail-title">${escape(job.title)}</h1><span class="pill ${escape(job.priority?.toLowerCase())}">${escape(job.priority)} PRIORITY</span><span class="pill">${escape(statusText(job.crewStatus))}</span>${job.pending?'<span class="pill draft">UNSYNCED DRAFT</span>':''}</div><div class="card"><h2>Job location</h2>${mapSnippet(job)}</div><div class="card"><h2>Hazards &amp; protection</h2><p class="hazard-note">${escape(job.hazardClass)} hazard${job.hazards?.length?' · '+job.hazards.map(escape).join('; '):''} · stop if the site is unsafe. Contact your supervisor before changing the approved task.</p><h3>Required PPE</h3><div class="tag-list">${(job.ppe||[]).map(item=>'<span class="tag">'+escape(item)+'</span>').join('') || '<span class="hint">Check the approved procedure.</span>'}</div><h3>Required certifications</h3><ul>${(job.requiredCertifications||[]).map(item=>'<li>'+escape(item)+'</li>').join('') || '<li>No additional certificate listed.</li>'}</ul></div><div class="card"><h2>Work steps</h2><ol class="steps">${(job.steps||[]).map(step=>'<li>'+escape(step)+'</li>').join('')}</ol><p class="hint">These are approved catalogue steps. Do not improvise safety or police actions; an authorised human decides.</p></div>${blocked?'<div class="card"><h2>Changed job · review required</h2><p class="hint">A server conflict or rejected draft stopped this job\'s outbox. Your drafts remain below; they will not overwrite newer human decisions.</p><button id="review-conflict" class="button secondary" type="button">Load current job for review</button><button id="discard-conflict" class="button secondary" type="button">Discard this job\'s unsent draft chain</button></div>':''}<div class="card"><h2>Update job status</h2><form id="status-form"><label for="job-status">Your status</label><select id="job-status">${['en-route','on-site','paused','complete','cannot-complete'].map(status=>'<option value="'+status+'"'+(job.crewStatus===status?' selected':'')+'>'+escape(statusText(status))+'</option>').join('')}</select><label for="status-reason">Reason / completion note</label><textarea id="status-reason" maxlength="1000" rows="2"></textarea><p class="hint">A reason is required for Paused or Cannot complete. This named worker action is audited. Complete records a crew completion report; the dispatcher must review and resolve the case. Resident communications still require human approval.</p><button class="button primary" type="submit"${blocked?' disabled':''}>Save status update</button></form></div><div class="card"><h2>Job notes</h2><p class="hint">Purpose: complete this approved job. Supervisors can read work evidence. Avoid names, identity numbers and sensitive personal details.</p>${(job.notes||[]).map(note=>'<p class="note">'+escape(note.text)+'<small>'+escape(date(note.at || note.createdAt || Date.now()))+'</small></p>').join('')}${draftNotes.map(note=>'<p class="note">'+escape(note.body.text)+'<small>Unsent draft · '+escape(note.state)+'</small></p>').join('')}<form id="note-form"><label for="job-note">Work note</label><textarea id="job-note" maxlength="2000" required rows="3"></textarea><button class="button secondary" type="submit"${blocked?' disabled':''}>Save note</button></form></div><div class="card"><h2>Work photo</h2><p class="hint">Purpose: evidence of this job or hazard only. Avoid people and sensitive details. The photo is re-encoded on this device; all EXIF/GPS metadata is removed. This web app does not preserve per-photo GPS metadata.</p><form id="photo-form"><label for="job-photo">Capture or choose a work photo</label><input id="job-photo" type="file" accept="image/jpeg,image/png,image/webp" capture="environment" required><img id="photo-preview" class="photo-preview" alt="Your metadata-stripped work photo preview" hidden><label class="check"><input id="photo-purpose" type="checkbox" required><span>I checked this photo for people and personal details and agree to processing for this work-evidence purpose.</span></label><button id="photo-save" class="button secondary" type="submit"${blocked?' disabled':''}>Save stripped photo</button></form><p id="photo-result" class="hint">${(job.photos||[]).length} saved photo record(s). ${app.outbox.filter(item=>item.jobId===job.id && item.action==='photos').length} unsent photo draft(s).</p></div>`;
    $('#status-form').addEventListener('submit',async event=>{event.preventDefault();const status=$('#job-status').value;const reason=$('#status-reason').value.trim();if(['paused','cannot-complete'].includes(status)&&reason.length<5) { message('Add a reason of at least 5 characters for this status.',true);$('#status-reason').focus();return; }await busy(event.currentTarget,context=>queueJob(job.id,'status',{status,reason:reason || null},context));});
    $('#note-form').addEventListener('submit',async event=>{event.preventDefault();await busy(event.currentTarget,context=>queueJob(job.id,'notes',{text:$('#job-note').value.trim()},context));});
    $('#job-photo').addEventListener('change',async event=>{
      clearPhoto();const context=identity(),photoEpoch=app.photoEpoch,input=event.target,preview=$('#photo-preview');
      try { const file=input.files[0];if(!file)return;const photo=await stripPhoto(file);requireCurrent(context);if(photoEpoch!==app.photoEpoch||app.selected!==job.id||!preview.isConnected)return;app.photo=photo;app.photoUrl=URL.createObjectURL(photo);preview.src=app.photoUrl;preview.hidden=false;$('#photo-result').textContent='Re-encoded on this device · all EXIF/GPS metadata removed · '+Math.round(photo.size/1024)+' KiB.'; }
      catch(error) { if(!error.stale&&current(context)&&input.isConnected) { message(error.message,true);input.value=''; } }
    });
    $('#photo-form').addEventListener('submit',async event=>{
      event.preventDefault();if(!app.photo) { message('Choose a valid work photo first.',true);return; }
      const photo=app.photo,photoEpoch=app.photoEpoch;await busy(event.currentTarget,async context=>{const bytes=new Uint8Array(await photo.arrayBuffer());requireCurrent(context);if(photoEpoch!==app.photoEpoch||app.selected!==job.id)return;let binary='';for(let i=0;i<bytes.length;i+=8192)binary+=String.fromCharCode(...bytes.subarray(i,i+8192));await queueJob(job.id,'photos',{contentType:'image/jpeg',dataBase64:btoa(binary),locationOptIn:false},context);if(current(context))clearPhoto();});
    });
    $('#review-conflict')?.addEventListener('click',async()=>{await busy($('#review-conflict'),async context=>{const result=await api('/api/v1/crew/me/jobs/'+encodeURIComponent(job.id),{},context);requireCurrent(context);app.jobs=app.jobs.map(item=>item.id===job.id?result:item);await cacheJobs(context);requireCurrent(context);renderDetail();message('Current server job loaded. Read the changed task and your drafts before discarding or recreating anything.');});});
    $('#discard-conflict')?.addEventListener('click',async()=>{const context=identity();if(!confirm('Discard all unsent status, note and photo drafts for this job after reviewing the current server version?'))return;try { const entries=app.outbox.filter(item=>item.jobId===job.id);for(const item of entries) { requireCurrent(context);await database.remove(item.id,()=>current(context)); }await refreshOutbox(context);requireCurrent(context);renderDetail();message('Unsent drafts for this job discarded by your explicit choice.'); }catch(error) { if(!error.stale&&current(context))message(error.message,true); }});
  }
  function clearPhoto() { app.photoEpoch++;if(app.photoUrl)URL.revokeObjectURL(app.photoUrl);app.photoUrl=null;app.photo=null; }
  async function stripPhoto(file) {
    if(!['image/jpeg','image/png','image/webp'].includes(file.type)||file.size>8*1024*1024)throw new Error('Choose a JPEG, PNG or WebP photo of at most 8 MiB.');
    const bitmap=await createImageBitmap(file);try {
      if(bitmap.width*bitmap.height>24000000)throw new Error('Choose a photo with at most 24 megapixels.');
      const scale=Math.min(1,1600/Math.max(bitmap.width,bitmap.height));const canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.round(bitmap.width*scale));canvas.height=Math.max(1,Math.round(bitmap.height*scale));canvas.getContext('2d').drawImage(bitmap,0,0,canvas.width,canvas.height);
      const photo=await new Promise(resolve=>canvas.toBlob(resolve,'image/jpeg',.82));if(!photo || photo.size>1400000)throw new Error('Photo is too large after stripping metadata; choose a smaller image.');return photo;
    }finally { bitmap.close(); }
  }
  async function busy(element,action) { const buttons=element.matches('button')?[element]:[...element.querySelectorAll('button')];if(buttons.some(button=>button.disabled))return;const context=identity();buttons.forEach(button=>button.disabled=true);try { await action(context); }catch(error) { if(!error.stale&&(current(context)||error.epoch===app.epoch))message(error.message,true); }finally { buttons.forEach(button=>{if(button.isConnected)button.disabled=false;});if(current(context))updateIndicators(); } }
  async function cacheJobs(context=identity()) { requireCurrent(context);const value={id:'jobs',partition:context.partition,jobs:structuredClone(app.jobs),generatedAt:app.jobsFreshAt || Date.now(),expiresAt:Date.now()+JOB_TTL};if(context.partition)await database.put(value,()=>current(context));requireCurrent(context); }
  async function refreshOutbox(context=identity()) { requireCurrent(context);const entries=context.partition?await database.list(context.partition):[];requireCurrent(context);app.outbox=entries;renderOutbox();renderJobs();updateIndicators(); }
  function renderOutbox() {
    $('#outbox-count').textContent=app.outbox.length;
    $('#outbox-list').innerHTML=app.outbox.map(item=>'<article class="outbox-item"><strong>'+escape(item.action || item.path.split('/').pop())+' · '+escape(item.state)+'</strong><span class="hint">'+escape(item.jobId?app.jobs.find(job=>job.id===item.jobId)?.referenceNumber || item.jobId:'Shift / consent stop')+' · attempts '+item.attempts+(item.nextAttemptAt>Date.now()?' · retry after '+escape(date(item.nextAttemptAt)):'')+'</span>'+(item.error?'<p>'+escape(item.error)+'</p>':'')+(item.action==='notes'?'<p>'+escape(item.body.text)+'</p>':'')+'</article>').join('') || '<p class="hint">All saved work has been acknowledged. No location is stored in this outbox.</p>';
  }
  async function queueJob(jobId,action,body,context=identity()) {
    requireCurrent(context);const job=app.jobs.find(item=>item.id===jobId);if(!job||!context.partition)throw new Error('Sign in to your own crew profile first.');
    const chain=app.outbox.filter(item=>item.jobId===jobId);if(chain.some(item=>item.state!=='pending'))throw new Error('Review the changed job before adding another draft.');
    if(app.outbox.length>=100)throw new Error('The 100-draft device limit is reached. Sync or review your existing outbox first.');
    const operationId=crypto.randomUUID();const item={id:operationId,partition:context.partition,kind:'job',jobId,action,path:'/api/v1/crew/me/jobs/'+encodeURIComponent(jobId)+'/'+action,method:'POST',body:{operationId,...body,expectedVersion:job.version+chain.length},createdAt:Date.now()+performance.now()%1,expiresAt:Date.now()+DRAFT_TTL,state:'pending',attempts:0,nextAttemptAt:0,error:null};
    await database.add(item,()=>current(context));requireCurrent(context);await refreshOutbox(context);requireCurrent(context);renderDetail();message(navigator.onLine?'Work saved to the outbox; waiting for server acknowledgement.':'Offline draft saved on this device. It has not changed the server job.');await flush(true,context);
  }
  async function queuePrivacy(path,body,method='POST',context=identity()) {
    requireCurrent(context);if(!context.partition)throw new Error('Sign in to your crew profile first.');const operationId=crypto.randomUUID();await database.add({id:operationId,partition:context.partition,kind:'privacy',path,method,body:{operationId,...body},createdAt:Date.now()+performance.now()%1,expiresAt:Date.now()+DRAFT_TTL,state:'pending',attempts:0,nextAttemptAt:0,error:null},()=>current(context));requireCurrent(context);await refreshOutbox(context);await flush(true,context);
  }
  async function flush(force=false,context=identity()) {
    requireCurrent(context);if(app.syncing||!app.live||!navigator.onLine||document.visibilityState!=='visible'||!context.partition)return;
    const work=async()=>{
      requireCurrent(context);if(app.syncing)return;app.syncing=context;let privacyChanged=false;
      try {
        await refreshOutbox(context);const stalled=new Set();const entries=app.outbox.slice().sort((a,b)=>Number(b.kind==='privacy')-Number(a.kind==='privacy')||a.createdAt-b.createdAt);
        for(const item of entries) {
          requireCurrent(context);if(item.partition!==context.partition)return;
          if(item.state!=='pending'||item.jobId&&stalled.has(item.jobId)||!force&&item.nextAttemptAt>Date.now()) { if(item.jobId)stalled.add(item.jobId);continue; }
          try {
            const result=await api(item.path,{method:item.method,body:JSON.stringify(item.body)},context);requireCurrent(context);await database.remove(item.id,()=>current(context));requireCurrent(context);
            if(result.job)app.jobs=app.jobs.map(job=>job.id===result.job.id?result.job:job);
            if(item.kind==='privacy')privacyChanged=true;
          }catch(error) {
            if(error.stale||!current(context))return;
            item.attempts++;item.lastAttemptAt=Date.now();item.error=error.message;
            if(error.status===409) { item.state='conflict';item.error='Job changed or operation content conflicted. Review required; no overwrite occurred.'; }
            else if(error.status && error.status<500 && ![408,429].includes(error.status))item.state='blocked';
            else if(item.attempts>=8)item.state='blocked';else item.nextAttemptAt=item.lastAttemptAt+retryDelay(item.attempts);
            await database.add(item,()=>current(context));requireCurrent(context);if(item.jobId)stalled.add(item.jobId);
            if(item.kind==='privacy')stopSharing('A privacy stop is awaiting server acknowledgement. Sharing stays stopped.');
          }
        }
        await cacheJobs(context);await refreshOutbox(context);requireCurrent(context);if(app.selected)renderDetail();if(privacyChanged)await loadMe(context);
      }catch(error) { if(!error.stale)throw error; }finally { if(app.syncing===context)app.syncing=false; }
    };
    if(navigator.locks)await navigator.locks.request('civicops-crew-outbox',{ifAvailable:true},async lock=>{if(lock&&current(context))await work();});else await work();
  }
  async function cacheProfile(context=identity()) { requireCurrent(context);const me=app.me;await database.put({id:'profile',partition:context.partition,pathBase:municipality,workerId:me.workerId,crewId:me.crewId,tenantId:me.tenantId,policy:me.policy,privacyNotice:me.privacyNotice,noticeVersion:me.noticeVersion,lastShiftId:me.shift?.id || null,demoMode:me.demoMode,expiresAt:Date.now()+JOB_TTL},()=>current(context));requireCurrent(context); }
  async function loadMe(context=identity()) {
    const me=await api('/api/v1/crew/me',{},context);requireCurrent(context);if(app.paired&&(me.workerId!==app.paired.workerId||me.tenantId!==app.paired.tenantId)) { await clearPrivate('The paired identity changed. Sign in again.',context);const error=new Error('Identity changed.');error.stale=true;throw error; }
    const partition=me.tenantId+':'+me.workerId;
    if(app.partition && app.partition!==partition) { context=invalidateIdentity(false);await database.clear(()=>current(context));requireCurrent(context); }
    if(app.watch!==null&&(gate.shift?.id!==me.shift?.id||gate.consent?.id!==me.consent?.id))stopSharing('The shift or consent record changed. Start sharing again explicitly after reviewing it.');
    if(app.watch!==null) { gate.shift=structuredClone(me.shift);gate.consent=structuredClone(me.consent); }
    app.partition=partition;app.me=me;app.offlineProfile=false;app.live=true;context=identity();await cacheProfile(context);await refreshOutbox(context);requireCurrent(context);
    if(!activeShift()||!activeConsent()||privacyPending())stopSharing('Location sharing requires an active shift and valid consent.');updateIndicators();return context;
  }
  async function refresh(initial=false) {
    if(app.refreshing)return;const owner={};app.refreshing=owner;let context=identity();
    try {
      if(!context.token) { const auth=await api('/auth/status',{},context);requireCurrent(context);app.auth=auth;context=identity(); }
      $('#demo-signin').hidden=!app.auth?.demoMode;$('#demo-banner').hidden=!(app.auth?.demoMode || app.me?.demoMode);
      $('#sso-signin').hidden=!app.auth?.oidcConfigured;$('#sso-note').hidden=!!app.auth?.oidcConfigured;
      $('#sso-signin').href=apiPath('/auth/login')+'?returnUrl='+encodeURIComponent(municipality+'/crew/');
      if(app.auth?.tenants)$('#demo-tenant').innerHTML=app.auth.tenants.map(tenant=>'<option value="'+escape(tenant.id)+'">'+escape(tenant.name)+'</option>').join('');
      context=await loadMe(context);requireCurrent(context);const response=await api('/api/v1/crew/me/jobs',{},context);requireCurrent(context);app.jobs=response.jobs;app.jobsFreshAt=Date.now();await cacheJobs(context);requireCurrent(context);renderJobs();if(app.selected)renderDetail();if(initial)$('#message').hidden=true;if(initial||app.screen==='signin')show('jobs',false);await flush(false,context);
    }catch(error) {
      if(error.stale||!current(context))return;
      stopSharing('Connection or authorisation unavailable; location sharing stopped.');gate.authoritative=false;app.live=false;
      if(error.offline) {
        const profile=await database.get('profile');requireCurrent(context);const jobs=await database.get('jobs');requireCurrent(context);
        const pairedPartition=app.paired?app.paired.tenantId+':'+app.paired.workerId:null;
        if(profile&&profile.pathBase===municipality&&profile.expiresAt>Date.now()&&(!context.partition||context.partition===profile.partition)&&(!pairedPartition||pairedPartition===profile.partition)) {
          app.partition=profile.partition;app.me={...profile,shift:null,consent:null};app.offlineProfile=true;app.jobs=jobs?.partition===profile.partition&&jobs.expiresAt>Date.now()?jobs.jobs:[];app.jobsFreshAt=jobs?.generatedAt || 0;context=identity();await refreshOutbox(context);requireCurrent(context);if(initial)show('jobs',false);renderJobs();
        }
        message('Offline · matching cached jobs and drafts may be available. Shift starts, location and SOS acknowledgement require connectivity.',true);
      }else if([401,403].includes(error.status)) { await clearPrivate('Crew sign-in is required. Use your assigned municipal identity or a supervisor pairing code.',context); }
      else message(error.message,true);
    }finally { if(app.refreshing===owner)app.refreshing=false;if(current(context))updateIndicators(); }
  }
  function updateIndicators() {
    const sharing=app.watch!==null&&gate.allowed();$('#sharing-indicator').classList.toggle('sharing',sharing);
    $('#sharing-state').textContent=sharing?'ON SHIFT · SHARING LOCATION · visible page only':app.offlineProfile?'Offline · shift state unverified · location not shared':activeShift()?'On shift · location is not shared':'Off shift · location is not shared';
    $('#connection').textContent=!navigator.onLine?'Offline · unsent drafts stay here':app.live?'Connected · '+(app.me?.demoMode?'synthetic work':'municipal crew'):'Crew sign-in required';$('#connection').classList.toggle('offline',!navigator.onLine || app.offlineProfile);
    $('#demo-banner').hidden=!(app.auth?.demoMode || app.me?.demoMode);
    $('#shift-summary-label').textContent=app.offlineProfile?'Offline shift unverified':activeShift()?'On shift':'Off shift';$('#shift-summary-note').textContent=sharing?'Sharing while this page is visible.':'Location sharing is optional.';
    $('#shift-description').textContent=app.offlineProfile?'Shift state is unverified offline. End uses the last acknowledged shift ID; no location is requested.':activeShift()?'Started '+date(app.me.shift.startedAt)+' · automatically ends by '+date(app.me.shift.expiresAt)+'.':'No server-confirmed active shift.';
    $('#shift-start').disabled=!app.live||activeShift()||privacyPending();$('#shift-end').disabled=!app.partition||!(app.me?.shift?.id || app.offlineProfile&&app.me?.lastShiftId)||app.outbox.some(item=>item.kind==='privacy'&&item.path.endsWith('/shifts/end'));
    $('#consent-grant').disabled=!app.live||activeConsent()||privacyPending();$('#consent-withdraw').disabled=!app.partition||app.outbox.some(item=>item.kind==='privacy'&&item.path.endsWith('/consent')&&item.method==='DELETE');
    $('#consent-status').textContent=privacyPending()?'A privacy stop is queued. Sharing stays stopped until the server acknowledges it.':app.offlineProfile?'Offline: consent state is unverified. You may queue withdrawal; sharing stays off.':activeConsent()?'Consent recorded '+date(app.me.consent.grantedAt)+'. You may withdraw at any time.':'Consent has not been granted or was withdrawn.';
    $('#sharing-start').disabled=!app.live||!activeShift()||!activeConsent()||privacyPending()||sharing||document.visibilityState!=='visible';$('#sharing-stop').disabled=!sharing;
    $('#history-load').disabled=!app.live;$('#history-export').disabled=!app.live;
    if(app.me?.policy) { const policy=app.me.policy;$('#policy-interval').textContent=policy.intervalSeconds+' seconds';$('#policy-retention').textContent=policy.retentionDays+' days';$('#policy-shift').textContent=policy.maxShiftHours+' hours';$('#policy-accuracy').textContent=policy.accuracyMeters+' metres'; }
    if(app.me?.privacyNotice)$('#privacy-notice').textContent=app.me.privacyNotice;
  }
  function stopSharing(reason) {
    gate.stop();app.locationEpoch++;if(app.watch!==null)navigator.geolocation?.clearWatch(app.watch);app.watch=null;app.lastPing=0;
    if(reason)$('#location-quality').textContent=reason;updateIndicators();
  }
  async function startSharing(context=identity()) {
    await loadMe(context);requireCurrent(context);if(!app.live||!activeShift()||!activeConsent()||privacyPending()||document.visibilityState!=='visible'||!navigator.onLine)throw new Error('A visible page, active server-confirmed shift and explicit consent are required.');
    if(!navigator.geolocation)throw new Error('Geolocation is unavailable. Continue your job without sharing and contact your supervisor.');
    gate.shift=structuredClone(app.me.shift);gate.consent=structuredClone(app.me.consent);gate.authoritative=true;gate.visible=true;gate.online=true;gate.requested=true;
    const session={context,epoch:++app.locationEpoch,shiftId:gate.shift.id,consentId:gate.consent.id,policy:structuredClone(app.me.policy)};app.lastPing=0;
    const valid=()=>current(context)&&session.epoch===app.locationEpoch;
    app.watch=navigator.geolocation.watchPosition(async position=>{
      if(!valid())return;gate.visible=document.visibilityState==='visible';gate.online=navigator.onLine;
      if(!gate.allowed()||app.me?.shift?.id!==session.shiftId||app.me?.consent?.id!==session.consentId) { stopSharing('Sharing stopped because a privacy condition changed.');return; }
      const policy=session.policy;const accuracy=position.coords.accuracy;
      if(!Number.isFinite(accuracy)||accuracy>policy.accuracyMeters) { $('#location-quality').textContent='Fix accuracy '+Math.round(accuracy)+'m exceeds the '+policy.accuracyMeters+'m policy; no ping sent.';return; }
      if(Date.now()-app.lastPing<policy.intervalSeconds*1000)return;
      app.lastPing=Date.now();const payload={operationId:crypto.randomUUID(),shiftId:session.shiftId,consentId:session.consentId,visibleIndicator:true,points:[{longitude:position.coords.longitude,latitude:position.coords.latitude,accuracyMeters:accuracy,capturedAt:new Date().toISOString()}]};
      try { await api('/api/v1/crew/locations',{method:'POST',body:JSON.stringify(payload)},context);if(valid()&&gate.allowed())$('#location-quality').textContent='Server acknowledged one on-shift ping · '+Math.round(accuracy)+'m accuracy · '+new Date().toLocaleTimeString('en-ZA')+'.'; }
      catch(error) { if(!valid()||error.stale)return;stopSharing('Location was not acknowledged; sharing stopped. No ping was queued.');message(error.message,true);if(error.status===403) { try { await loadMe(context); }catch(failure) { if(!failure.stale&&current(context))message(failure.message,true); } } }
    },error=>{if(!valid())return;stopSharing('Browser location permission or a valid fix is unavailable. Sharing stopped.');message(error.message || 'Location permission denied.',true);},{enableHighAccuracy:session.policy.accuracyMeters<=100,maximumAge:0,timeout:15000});
    $('#location-quality').textContent='Waiting for a fresh browser fix. Only policy-compliant fixes are sent.';message('Location sharing started while this page is visible. It stops when hidden, offline or withdrawn.');updateIndicators();
  }
  async function clearPrivate(notice,context=identity()) {
    requireCurrent(context);const next=invalidateIdentity();await database.clear(()=>current(next));requireCurrent(next);if(notice)message(notice);try { const auth=await api('/auth/status',{},next);requireCurrent(next);app.auth=auth; }catch { /* Offline sign-out still clears all private state. */ }
  }
  document.querySelectorAll('[data-screen]').forEach(button=>button.addEventListener('click',()=>show(button.dataset.screen)));
  $('#sos-open').addEventListener('click',()=>show('sos'));$('#refresh').addEventListener('click',()=>refresh());$('#job-filter').addEventListener('change',renderJobs);$('#sync-now').addEventListener('click',()=>busy($('#sync-now'),context=>flush(true,context)));
  $('#sso-signin').addEventListener('click',async event=>{event.preventDefault();const href=event.currentTarget.href;try { const context=await beginIdentity();requireCurrent(context);location.assign(href); }catch(error) { if(!error.stale)message(error.message,true); }});
  $('#bootstrap-form').addEventListener('submit',async event=>{event.preventDefault();await busy(event.currentTarget,async()=>{const token=$('#bootstrap-token').value;$('#bootstrap-token').value='';const context=await beginIdentity();await api('/auth/bootstrap-login',{method:'POST',body:JSON.stringify({token})},context);requireCurrent(context);await beginIdentity();await refresh(true);});});
  $('#demo-form').addEventListener('submit',async event=>{event.preventDefault();await busy(event.currentTarget,async()=>{const body={role:'crew',tenantId:$('#demo-tenant').value,crewId:$('#demo-crew').value.trim()};const context=await beginIdentity();await api('/auth/demo-login',{method:'POST',body:JSON.stringify(body)},context);requireCurrent(context);await beginIdentity();await refresh(true);});});
  $('#pair-form').addEventListener('submit',async event=>{event.preventDefault();await busy(event.currentTarget,async()=>{const body={tenantId:$('#pair-tenant').value.trim(),pairingId:$('#pair-id').value.trim(),code:$('#pair-code').value.trim()};$('#pair-code').value='';const context=await beginIdentity();const result=await api('/api/v1/crew/pairing/exchange',{method:'POST',body:JSON.stringify(body)},context);requireCurrent(context);await beginIdentity();app.token=result.token;app.paired={token:result.token,expiresAt:result.expiresAt,tenantId:result.tenantId,workerId:result.workerId};sessionStorage.setItem('civicops-crew-paired-session',JSON.stringify(app.paired));const epoch=app.epoch;await refresh(true);if(epoch===app.epoch&&app.live)message('Paired crew session connected until '+date(result.expiresAt)+'. Access stays in this browser tab and is verified before sync.');});});
  $('#shift-start').addEventListener('click',()=>busy($('#shift-start'),async context=>{if(!app.live)throw new Error('A shift must be acknowledged online.');app.shiftKey ||= crypto.randomUUID();await api('/api/v1/crew/shifts/start',{method:'POST',body:JSON.stringify({operationId:app.shiftKey})},context);requireCurrent(context);app.shiftKey=null;await loadMe(context);requireCurrent(context);message('Shift started. Location stays off until you explicitly start sharing.');}));
  $('#shift-end').addEventListener('click',()=>busy($('#shift-end'),async context=>{const shiftId=app.me?.shift?.id || app.me?.lastShiftId;stopSharing('Shift ended locally; no more location is collected.');if(app.me?.shift)app.me.shift.endedAt=new Date().toISOString();await queuePrivacy('/api/v1/crew/shifts/end',{shiftId},'POST',context);requireCurrent(context);message(privacyPending()?'Sharing stopped immediately. Shift end awaits connectivity and server acknowledgement.':'Shift ended and acknowledged. Location sharing is off.');}));
  $('#consent-form').addEventListener('submit',async event=>{event.preventDefault();await busy(event.currentTarget,async context=>{await api('/api/v1/crew/consent',{method:'POST',body:JSON.stringify({operationId:crypto.randomUUID(),noticeVersion:app.me.noticeVersion,accepted:$('#consent-check').checked})},context);requireCurrent(context);$('#consent-check').checked=false;await loadMe(context);requireCurrent(context);message('Explicit consent recorded. Choose Start sharing separately when ready.');});});
  $('#consent-withdraw').addEventListener('click',()=>busy($('#consent-withdraw'),async context=>{stopSharing('Consent withdrawn locally; collection stopped immediately.');if(app.me?.consent)app.me.consent.withdrawnAt=new Date().toISOString();await queuePrivacy('/api/v1/crew/consent',{},'DELETE',context);requireCurrent(context);message(privacyPending()?'Collection stopped immediately. Withdrawal is queued for server acknowledgement when online.':'Consent withdrawal acknowledged. Location sharing is off.');}));
  $('#sharing-start').addEventListener('click',()=>busy($('#sharing-start'),startSharing));$('#sharing-stop').addEventListener('click',()=>stopSharing('You stopped sharing. Resuming requires an explicit choice.'));
  $('#history-load').addEventListener('click',()=>busy($('#history-load'),async context=>{const history=await api('/api/v1/crew/me/locations',{},context);requireCurrent(context);$('#location-history').innerHTML='<p class="hint">'+history.total+' own retained ping(s) · raw retention '+history.retentionDays+' days · showing latest '+history.points.length+'.</p>'+history.points.map(point=>'<div class="history-point">'+escape(date(point.capturedAt))+'<br>'+Number(point.latitude).toFixed(5)+', '+Number(point.longitude).toFixed(5)+' · '+Math.round(point.accuracyMeters)+'m accuracy</div>').join('');}));
  $('#history-export').addEventListener('click',()=>busy($('#history-export'),async context=>{const history=await api('/api/v1/crew/me/locations/export',{},context);requireCurrent(context);const url=URL.createObjectURL(new Blob([JSON.stringify(history,null,2)],{type:'application/json'}));const link=document.createElement('a');link.href=url;link.download='civicops-my-location-history.json';document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);message('Your retained history was exported. Store the file privately.');}));
  $('#clear-offline').addEventListener('click',()=>busy($('#clear-offline'),async context=>{if(!confirm('Clear all cached jobs and unsent work drafts on this device? Unsent work will be lost.'))return;stopSharing('Sharing stopped before clearing local data.');await database.clear(()=>current(context));requireCurrent(context);app.jobs=[];app.outbox=[];await refreshOutbox(context);requireCurrent(context);if(app.live) { await cacheProfile(context);await refresh(); }if(current(context))message('Private offline jobs and unsent drafts were cleared by your choice.');}));
  $('#signout').addEventListener('click',()=>busy($('#signout'),async()=>{
    const previous={token:app.token,csrfToken:app.auth?.csrfToken,shiftId:app.me?.shift?.id,consent:activeConsent(),authenticated:app.auth?.authenticated,live:app.live};const context=await beginIdentity();const credential={...context,token:previous.token,csrfToken:previous.csrfToken};let unacknowledged=false;
    try { if(previous.live&&previous.shiftId)await api('/api/v1/crew/shifts/end',{method:'POST',body:JSON.stringify({operationId:crypto.randomUUID(),shiftId:previous.shiftId})},credential);if(previous.live&&previous.consent)await api('/api/v1/crew/consent',{method:'DELETE',body:JSON.stringify({operationId:crypto.randomUUID()})},credential);if(!previous.token&&previous.authenticated)await api('/auth/logout',{method:'POST',body:'{}'},credential); }
    catch(error) { if(error.stale)return;unacknowledged=true; }requireCurrent(context);message(unacknowledged?'Signed out locally; private data cleared and collection stopped. Server end/withdrawal could not be acknowledged. Reconnect or ask your supervisor; shifts also expire automatically.':'Signed out; sharing stopped and private offline data cleared.');try { const auth=await api('/auth/status',{},context);requireCurrent(context);app.auth=auth; }catch { /* Remain locally signed out. */ }
  }));
  const crewSos={timer:null,startedAt:0,progress:$('#sos-progress'),alertId:null,cancelTimer:null};
  const sosStatus=message=>{const target=$('#sos-result');target.textContent=message;target.classList.remove('error');};
  const resetSosHold=()=>{if(crewSos.timer){clearInterval(crewSos.timer);crewSos.timer=null;}crewSos.startedAt=0;if(crewSos.progress)crewSos.progress.style.width='0%';};
  const stopSosHold=()=>{if(crewSos.timer){resetSosHold();sosStatus('SOS hold cancelled. No alert was sent.');}};
  const startSosHold=event=>{if(event?.pointerType==='mouse'&&event.button!==0)return;if(crewSos.timer||crewSos.alertId||$('#sos-submit').disabled)return;crewSos.startedAt=performance.now();sosStatus('Keep holding… 3 seconds is required to activate SOS.');crewSos.timer=setInterval(()=>{const progress=Math.min(1,(performance.now()-crewSos.startedAt)/3000);crewSos.progress.style.width=Math.round(progress*100)+'%';if(progress>=1){resetSosHold();triggerSos();}},40);};
  const shareSosLocation=async(id,context)=>{const sharing=app.watch!==null&&gate.allowed()&&document.visibilityState==='visible'&&app.me?.consent?.id;if(!sharing||!navigator.geolocation){$('#sos-address').focus();sosStatus($('#sos-silent').checked?'Silent SOS recorded. The control room is alerted. This device shows no alarm. Share a landmark if you can; location sensors stay off.':'SOS is active. Location sensors stay off until visible sharing is already on. Share an address or landmark.');return;}navigator.geolocation.getCurrentPosition(async position=>{if(crewSos.alertId!==id||!current(context))return;try{const response=await api('/api/v1/sos/'+encodeURIComponent(id)+'/location',{method:'POST',body:JSON.stringify({operationId:crypto.randomUUID(),location:{longitude:position.coords.longitude,latitude:position.coords.latitude},accuracyMeters:Number.isFinite(position.coords.accuracy)?position.coords.accuracy:null,addressOrLandmark:null,visibleIndicator:true,consentId:app.me.consent.id})},context);requireCurrent(context);sosStatus(response.message||'One on-shift point shared for this active SOS window. The visible sharing indicator stays on.');}catch(error){if(!error.stale&&current(context)){sosStatus(error.message);$('#sos-address').focus();}}},()=>{$('#sos-address').focus();sosStatus('SOS is active. Share an address or landmark so the control room can locate you.');},{enableHighAccuracy:false,maximumAge:0,timeout:10000});};
  const triggerSos=async()=>{const context=identity();$('#sos-submit').disabled=true;try{const result=await api('/api/v1/sos',{method:'POST',body:JSON.stringify({operationId:crypto.randomUUID(),channel:'crew-pwa',note:$('#sos-note').value.trim()||null,contact:null,addressOrLandmark:null,silent:$('#sos-silent').checked,location:null,accuracyMeters:null,batteryPercent:null})},context);requireCurrent(context);crewSos.alertId=result.alert?.id;if(!crewSos.alertId)throw new Error('The control room did not return an SOS reference.');sosStatus(result.message||'SOS alert sent to the municipal control room. A human operator must acknowledge it.');$('#sos-cancel').hidden=false;crewSos.cancelTimer=setTimeout(()=>$('#sos-cancel').hidden=true,10000);if(result.locationRequired)$('#sos-address').focus();shareSosLocation(crewSos.alertId,context);}catch(error){if(!error.stale&&current(context)){sosStatus(error.message);$('#sos-submit').disabled=false;}}};
  $('#sos-submit').addEventListener('pointerdown',startSosHold);$('#sos-submit').addEventListener('pointerup',stopSosHold);$('#sos-submit').addEventListener('pointercancel',stopSosHold);$('#sos-submit').addEventListener('pointerleave',stopSosHold);$('#sos-submit').addEventListener('keydown',event=>{if((event.key===' '||event.key==='Enter')&&!event.repeat){event.preventDefault();startSosHold(event);}});$('#sos-submit').addEventListener('keyup',event=>{if(event.key===' '||event.key==='Enter')stopSosHold();});
  $('#sos-address-submit')?.addEventListener('click',async()=>{const context=identity(),address=$('#sos-address').value.trim();if(!address||!crewSos.alertId){$('#sos-address').focus();return;}const button=$('#sos-address-submit');button.disabled=true;try{const result=await api('/api/v1/sos/'+encodeURIComponent(crewSos.alertId)+'/location',{method:'POST',body:JSON.stringify({operationId:crypto.randomUUID(),location:null,accuracyMeters:null,addressOrLandmark:address})},context);requireCurrent(context);sosStatus(result.message||'Your landmark was shared for this active SOS window.');}catch(error){if(!error.stale&&current(context))sosStatus(error.message);}finally{button.disabled=false;}});
  $('#sos-cancel').addEventListener('click',async event=>{if(!crewSos.alertId)return;const context=identity(),button=event.currentTarget;button.disabled=true;try{await api('/api/v1/sos/'+encodeURIComponent(crewSos.alertId)+'/cancel',{method:'POST',body:JSON.stringify({operationId:crypto.randomUUID(),note:'Crew member cancelled during the confirmation window.'})},context);requireCurrent(context);crewSos.alertId=null;button.hidden=true;$('#sos-submit').disabled=false;sosStatus('SOS alert cancelled. No dispatch action was taken.');}catch(error){if(!error.stale&&current(context))sosStatus(error.message);button.disabled=false;}});
  $('#sos-form').addEventListener('submit',event=>event.preventDefault());
  $('#crew-language').addEventListener('change',()=>{app.language=$('#crew-language').value;localStorage.setItem('civicops-crew-language',app.language);translate();});
  window.addEventListener('beforeinstallprompt',event=>{event.preventDefault();app.install=event;$('#install-app').hidden=false;});$('#install-app').addEventListener('click',async()=>{if(app.install) { await app.install.prompt();app.install=null;$('#install-app').hidden=true; }});
  document.addEventListener('visibilitychange',()=>{if(document.visibilityState!=='visible')stopSharing('Page hidden; location sharing stopped. Returning requires another explicit start.');else { updateIndicators();refresh().catch(error=>{if(!error.stale)message(error.message,true);}); }});
  window.addEventListener('pagehide',()=>stopSharing('Page closed; location sharing stopped.'));window.addEventListener('offline',()=>{stopSharing('Offline; location collection stopped. No pings will be queued.');app.live=false;updateIndicators();});window.addEventListener('online',()=>refresh());
  navigator.serviceWorker?.addEventListener('message',event=>{if(event.data?.type==='crew:sync-needed')flush().catch(error=>{if(!error.stale)message(error.message,true);});});
  setInterval(()=>{if(app.paired&&Date.parse(app.paired.expiresAt)<=Date.now()) { clearPrivate('Paired crew access expired. Private data was cleared; ask your supervisor to pair again.').catch(error=>{if(!error.stale)message(error.message,true);});return; }if(app.watch!==null&&!gate.allowed())stopSharing('The shift or consent expired; sharing stopped automatically.');},1000);
  setInterval(()=>{if(document.visibilityState==='visible'&&navigator.onLine&&app.partition)flush().catch(error=>{if(!error.stale)message(error.message,true);});},15000);
  setInterval(()=>{if(document.visibilityState==='visible'&&navigator.onLine&&app.live)refresh().catch(error=>{if(error.stale)return;stopSharing('Server privacy state could not be checked; sharing stopped.');message(error.message,true);});},60000);
  setInterval(()=>database.purge().then(removed=>{if(removed)refreshOutbox().catch(()=>{});}).catch(error=>{if(!error.stale)message(error.message,true);}),3600000);
  // Diagnostics expose privacy state and queue counts only, never credentials or location samples. Paired access is tab-scoped sessionStorage, not IndexedDB or service-worker state.
  window.CivicCrew=Object.freeze({refresh,flush,diagnostics:()=>({live:app.live,offlineProfile:app.offlineProfile,screen:app.screen,sharing:app.watch!==null&&gate.allowed(),queueCount:app.outbox.length,partition:app.partition,epoch:app.epoch,refreshing:!!app.refreshing})});
  (async()=>{try { await database.purge();try { const paired=JSON.parse(sessionStorage.getItem('civicops-crew-paired-session') || 'null');if(paired&&Date.parse(paired.expiresAt)>Date.now()&&typeof paired.token==='string'&&typeof paired.workerId==='string'&&typeof paired.tenantId==='string') { app.epoch++;stopSharing('Restored credentials require verification; sharing stays off.');app.paired=paired;app.token=paired.token; }else if(paired) { sessionStorage.removeItem('civicops-crew-paired-session');await database.clear(); } }catch { sessionStorage.removeItem('civicops-crew-paired-session');await database.clear(); }app.strings=await fetch('/crew/strings.json').then(response=>response.json());const saved=localStorage.getItem('civicops-crew-language');if(['en','af','zu'].includes(saved))app.language=saved;$('#crew-language').value=app.language;translate();if('serviceWorker'in navigator)navigator.serviceWorker.register(apiPath('/crew/service-worker.js'),{scope:apiPath('/crew/')}).catch(()=>message('Offline installation is unavailable in this browser.',true));await refresh(true); }catch(error) { message(error.message,true); }})();
})();
