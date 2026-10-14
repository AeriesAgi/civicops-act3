#!/usr/bin/env node
// Synthetic signed webhooks and actual staff browser views. Downloads nothing and calls no provider.
import assert from 'node:assert/strict';
import {createHmac,randomUUID} from 'node:crypto';
import {mkdir,readFile,writeFile,stat} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

await (async function () {
  const base=process.env.CIVICOPS_BROWSER_BASE_URL || 'http://127.0.0.1:5088';const origin=new URL(base);
  assert(['127.0.0.1','localhost','[::1]'].includes(origin.hostname),'Channel smoke is restricted to a local synthetic server.');
  const modulePath=process.env.CIVICOPS_PLAYWRIGHT_MODULE;
  const {chromium}=await import(modulePath ? pathToFileURL(modulePath).href : 'playwright-core');
  const axe=await readFile(process.env.CIVICOPS_AXE_CORE_PATH || (modulePath ? path.resolve(path.dirname(modulePath),'../axe-core/axe.min.js') : path.resolve('node_modules/axe-core/axe.min.js')),'utf8');
  const output=path.join(process.env.TMPDIR || tmpdir(),'channels-browser-smoke');
  const media=process.env.CIVICOPS_UPDATE_MEDIA==='1' ? path.resolve('docs/media') : output;
  await mkdir(output,{recursive:true});await mkdir(media,{recursive:true});
  const browser=await chromium.launch({executablePath:process.env.CIVICOPS_CHROME_PATH || '/usr/bin/google-chrome',headless:true,args:['--no-sandbox','--disable-dev-shm-usage']});
  const context=await browser.newContext({viewport:{width:1440,height:1120},locale:'en-ZA',timezoneId:'Africa/Johannesburg',colorScheme:'light',reducedMotion:'reduce',serviceWorkers:'block'});
  const checks=[];const errors=[];const csp=[];const outside=[];const screenshots=[];const accessibility=[];
  const check=(name,result,detail='')=>{assert(result,name+(detail?': '+detail:''));checks.push(name);};
  await context.route('**/*',route=>{if(new URL(route.request().url()).origin===origin.origin)return route.continue();outside.push(route.request().url());return route.abort('blockedbyclient');});
  await context.exposeBinding('__channelCsp',(_,event)=>csp.push(event));
  await context.addInitScript(()=>document.addEventListener('securitypolicyviolation',event=>window.__channelCsp({directive:event.effectiveDirective,blocked:event.blockedURI})));
  const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
  const providerPhones={'twilio-sms':'+27000000101',clickatell:'+27000000102',whatsapp:'+27000000103','twilio-voice':'+27000000104',sip:'+27000000105',elevenlabs:'+27000000106'};
  const fixtures=new Map();
  async function request(url,body,method='POST',headers={}) {
    const response=await context.request.fetch(base+url,{method,headers:{'Content-Type':'application/json','Authorization':'Bearer test-smoke-dispatch-token',...headers},...(body===undefined?{}:{data:body})});
    return {status:response.status(),data:await response.json().catch(()=>({}))};
  }
  async function must(url,body,method='POST',headers={}) {
    const response=await request(url,body,method,headers);assert(response.status>=200&&response.status<300,url+' '+response.status+' '+JSON.stringify(response.data));return response.data;
  }
  async function browserApi(url,body,method='POST') {
    return page.evaluate(async({url,body,method})=>{const identity=await fetch('/auth/status',{cache:'no-store'}).then(response=>response.json());const response=await fetch(url,{method,credentials:'same-origin',cache:'no-store',headers:{'Content-Type':'application/json',...(identity.csrfToken?{'X-CivicOps-CSRF':identity.csrfToken}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return {status:response.status,data:await response.json().catch(()=>({}))};},{url,body,method});
  }
  function signed(provider,payload,nonce='synthetic-'+randomUUID(),timestamp=String(Math.floor(Date.now()/1000)),signature) {
    const body=JSON.stringify(payload);const hmac=createHmac('sha256','synthetic-channel-signing-key').update(timestamp+'.'+nonce+'.'+provider+'.default.'+body).digest('hex');
    return {body,headers:{'X-CivicOps-Timestamp':timestamp,'X-CivicOps-Nonce':nonce,'X-CivicOps-Signature':signature || 'sha256='+hmac}};
  }
  async function inbound(provider,payload,options={}) {
    const envelope=signed(provider,payload,options.nonce,options.timestamp,options.signature);
    return request('/api/v1/channels/simulated/'+provider,envelope.body,'POST',envelope.headers);
  }
  async function command(provider,text) {
    const fixture={...fixtures.get(provider),text,providerMessageId:'synthetic-command-'+randomUUID(),receivedAt:new Date().toISOString(),transcriptSummary:null,humanHandoffRequested:false,callbackRequested:false,mediaReferences:null,location:null,locationShared:false};
    return inbound(provider,fixture);
  }
  async function scan(name) {
    if(!await page.evaluate(()=>!!window.axe))await page.evaluate(axe);
    const report=await page.evaluate(()=>axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']}}));
    const file=path.join(output,'axe-'+name+'.json');await writeFile(file,JSON.stringify(report,null,2));
    accessibility.push({name,violations:report.violations.length,passedRules:report.passes.length,file});
    check(name+' has zero WCAG2.2 A/AA violations',report.violations.length===0,JSON.stringify(report.violations.map(item=>({id:item.id,nodes:item.nodes.map(node=>({target:node.target,summary:node.failureSummary}))}))));
  }
  async function capture(name,locator=page) {
    await page.evaluate(async()=>{document.activeElement?.blur();scrollTo(0,0);await document.fonts.ready;await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));});
    const file=path.join(media,name+'.png');await locator.screenshot({path:file,animations:'disabled'});screenshots.push(file);
  }
  async function role(name,tenant='default') {
    await page.locator('#staff-access').evaluate(element=>element.open=true);await page.locator('#demo-role').selectOption(name);await page.locator('#demo-tenant').selectOption(tenant);await page.locator('#demo-login-form button').click();
    await page.waitForFunction(({name,tenant})=>state.auth?.authenticated && state.auth.roles.includes(name) && state.auth.tenantId===tenant && !document.querySelector('#demo-login-form button').disabled,{name,tenant});
    await page.locator('#staff-access').evaluate(element=>element.open=false);
  }
  const noOverflow=()=>page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth);
  async function openConversation(id) {await page.locator('[data-conversation="'+id+'"]').click();await page.waitForFunction(id=>CivicOpsChannels.diagnostics().detailId===id && !!document.querySelector('#inbox-reply-form'),id);}
  try {
    const health=await must('/healthz',undefined,'GET');check('Throwaway server is explicit demo',health.demoMode===true);
    const readiness=await must('/api/v1/channels/readiness',undefined,'GET');check('Readiness honestly simulates providers and disables external delivery',readiness.demoMode===true && readiness.externalSendsEnabled===false && readiness.channels.length>=6 && readiness.channels.every(item=>item.mode==='simulated'));
    const descriptions=[
      ['twilio-sms','sms','en','SYNTHETIC: blocked stormwater drain beside the fictional orange training shed.'],
      ['clickatell','sms','en','SYNTHETIC: deep road pothole beside the fictional blue municipal exercise gate.'],
      ['whatsapp','whatsapp','zu','SYNTHETIC: ukuvuza kwamanzi eduze kwendawo yokuqeqesha eqanjiwe. Water pipe leaking at the fictional green exercise site.'],
      ['twilio-voice','voice','af','SINTETIES: die straatlig werk nie by die denkbeeldige geel oefensentrum nie.'],
      ['sip','voice','en','SYNTHETIC: debris blocking a road drain at the fictional violet municipal exercise site.'],
      ['elevenlabs','voice','en','SYNTHETIC: a traffic signal is broken at the fictional silver training junction.']
    ];
    for(const [provider,name,languageGuess,text] of descriptions)fixtures.set(provider,{channel:name,provider,from:providerPhones[provider],to:'+27000000999',text,languageGuess,receivedAt:new Date().toISOString(),providerMessageId:'synthetic-report-'+provider,privacyConsentAccepted:true,privacyConsentVersion:'civicops-popia-v1',...(name==='voice'?{transcriptSummary:text,humanHandoffRequested:true,callbackRequested:true}:{}),...(name==='whatsapp'?{location:{latitude:-29.915,longitude:30.985},locationShared:true,mediaReferences:['synthetic-provider-media-01']}:{})});
    const invalid=await inbound('twilio-sms',fixtures.get('twilio-sms'),{signature:'sha256='+'0'.repeat(64)});check('Invalid HMAC is rejected before intake',invalid.status===401);
    const stale=await inbound('twilio-sms',fixtures.get('twilio-sms'),{timestamp:String(Math.floor(Date.now()/1000)-600)});check('Expired signed webhook is rejected',stale.status===401);
    const prohibited=await inbound('twilio-sms',{...fixtures.get('twilio-sms'),faceEmbedding:[0.1,0.2]});check('Normalized intake refuses a prohibited biometric field',prohibited.status===400);
    const results=new Map();
    let reusedNonce;
    for(const [provider] of descriptions) {
      const nonce='synthetic-'+randomUUID();if(provider==='twilio-sms')reusedNonce=nonce;
      const response=await inbound(provider,fixtures.get(provider),{nonce});check(provider+' valid signed synthetic webhook creates a reviewed case',response.status>=200&&response.status<300 && response.data.received===true && response.data.simulated===true && !!response.data.referenceNumber,JSON.stringify(response));results.set(provider,response.data);
    }
    const replay=await inbound('twilio-sms',fixtures.get('twilio-sms'),{nonce:reusedNonce});check('Replayed timestamp/nonce is rejected',replay.status===409 || replay.status===401);
    const sameMessage=await inbound('twilio-sms',fixtures.get('twilio-sms'));check('Fresh signed retry is idempotent on provider message id',sameMessage.status===200 && sameMessage.data.replayed===true && sameMessage.data.referenceNumber===results.get('twilio-sms').referenceNumber);
    const cases=await must('/api/incidents',undefined,'GET');
    for(const [provider,data] of results) {
      const incident=cases.find(item=>item.referenceNumber===data.referenceNumber);check(provider+' enters the same triage pipeline with human review',!!incident && incident.needsHumanReview===true && incident.status==='PendingApproval' && incident.agentRuns.some(run=>run.agentName==='TriageAgent'));
    }
    const waCase=cases.find(item=>item.referenceNumber===results.get('whatsapp').referenceNumber);check('Volunteered WhatsApp location reaches the case with explicit sharing',waCase.locationShared===true && waCase.location.latitude===-29.915);
    const conversations=await must('/api/v1/channels/conversations',undefined,'GET');
    check('Resident thread list masks all synthetic numbers and omits timeline bodies',conversations.length===6 && conversations.every(item=>item.residentLabel.includes('••••') && item.messages.length===0) && Object.values(providerPhones).every(phone=>!JSON.stringify(conversations).includes(phone)));
    for(const [provider,data] of results) {
      const detail=await must('/api/v1/channels/conversations/'+data.conversationId,undefined,'GET');check(provider+' thread links to its case and consent notice',detail.caseReferences.includes(data.referenceNumber) && detail.consentAccepted && detail.messages.some(message=>message.templateKey==='channel.notice' || /POPIA|purpose|privacy|privaatheid|ubumfihlo|consent|ukuvuma|toestemming/i.test(message.text)));
      if(provider.includes('voice') || ['sip','elevenlabs'].includes(provider))check(provider+' failover is visible in human call-taker queue',detail.humanQueue && detail.callbackRequested);
    }
    const stop=await command('twilio-sms','STOP');check('STOP is handled as a channel consent command',stop.status===200 && /stop|opt|suppress|unsubscribe/i.test(stop.data.command || stop.data.reply || ''));
    const suppressed=await must('/api/v1/channels/conversations/'+results.get('twilio-sms').conversationId,undefined,'GET');check('Suppression is retained for this channel and resident',suppressed.suppressed===true);
    const templates=await must('/api/v1/channels/templates',undefined,'GET');check('Five versioned templates each cover en/af/zu',templates.length===15 && ['channel.notice','case.received','case.update','case.resolved','case.request-info'].every(key=>['en','af','zu'].every(language=>templates.some(item=>item.key===key && item.language===language && item.version>=1))));
    const template=templates.find(item=>item.key==='case.received' && item.language==='en');
    const blocked=await request('/api/v1/channels/conversations/'+suppressed.id+'/reply',{templateKey:template.key,language:'en',variables:{reference:suppressed.caseReferences[0],update:'Synthetic human review.',request:'Synthetic safe information request.'},expectedTemplateVersion:template.version,expectedConversationVersion:suppressed.version,reason:'Synthetic guard verifies resident suppression.',operationId:randomUUID()});check('Human outbound is blocked to an opted-out resident',blocked.status===403 || blocked.status===409 || blocked.status===400);
    const start=await command('twilio-sms','START');check('START re-subscribes the same resident channel',start.status===200 && (await must('/api/v1/channels/conversations/'+suppressed.id,undefined,'GET')).suppressed===false);
    const help=await command('twilio-sms','HELP');check('HELP provides bounded municipal commands',help.status===200 && /STATUS/i.test(help.data.reply) && /STOP/i.test(help.data.reply));
    const status=await command('twilio-sms','STATUS '+results.get('twilio-sms').referenceNumber);check('STATUS returns the public case reference and update',status.status===200 && status.data.reply.includes(results.get('twilio-sms').referenceNumber) && !status.data.reply.includes('fictional orange'));
    const statusPrompt=await command('whatsapp','STATUS');check('Check-my-case button command prompts for a reference without creating a case',statusPrompt.status===200 && !!statusPrompt.data.reply && !statusPrompt.data.referenceNumber);
    const report=await command('twilio-sms','REPORT');check('REPORT prompts for a description without inventing a case',report.status===200 && !!report.data.reply && !report.data.referenceNumber);
    const sos=await command('whatsapp','SOS');check('SOS is a truthful emergency-number stub',sos.status===200 && ['10111','10177','112'].every(number=>sos.data.reply.includes(number)) && /stub|does not|cannot|emergency/i.test(sos.data.reply));
    for(const commandText of ['STOPALL','UNSUBSCRIBE']) {const response=await command('clickatell',commandText);check(commandText+' suppresses Clickatell channel',response.status===200 && (await must('/api/v1/channels/conversations/'+results.get('clickatell').conversationId,undefined,'GET')).suppressed===true);await command('clickatell','START');}
    const af=await command('twilio-voice','STOP');check('Voice opt-out applies to voice resident thread',af.status===200 && (await must('/api/v1/channels/conversations/'+results.get('twilio-voice').conversationId,undefined,'GET')).suppressed===true);await command('twilio-voice','START');
    const afStop=await command('twilio-voice','HOU OP');check('Afrikaans HOU OP suppresses the same voice channel',afStop.status===200 && (await must('/api/v1/channels/conversations/'+results.get('twilio-voice').conversationId,undefined,'GET')).suppressed===true);await command('twilio-voice','HERVAT');
    const zuStop=await command('whatsapp','YIMA');check('IsiZulu YIMA suppresses WhatsApp for this resident',zuStop.status===200 && (await must('/api/v1/channels/conversations/'+results.get('whatsapp').conversationId,undefined,'GET')).suppressed===true);const zuStart=await command('whatsapp','QALA');check('IsiZulu QALA restores consented WhatsApp messaging',zuStart.status===200 && (await must('/api/v1/channels/conversations/'+results.get('whatsapp').conversationId,undefined,'GET')).suppressed===false);
    await page.goto(base+'/#inbox',{waitUntil:'domcontentloaded'});await page.waitForFunction(()=>!!window.CivicOpsChannels && state.auth!==null);
    await role('supervisor');await page.locator('[data-view="inbox"]').click();await page.waitForSelector('#inbox-content:not([hidden])');await page.waitForFunction(()=>document.querySelectorAll('[data-conversation]').length===6);
    check('Unified inbox renders all six masked resident threads and four activity cards',await page.locator('[data-conversation]').count()===6 && await page.locator('#inbox-metrics .kpi').count()===4);
    check('Inbox is explicitly synthetic and requires human decisions',(await page.locator('#inbox-data-label').textContent()).includes('SYNTHETIC') && (await page.locator('.inbox-boundary').textContent()).includes('A human approves'));
    await openConversation(results.get('twilio-voice').conversationId);check('Voice summary, linked case and callback queue are visible',(await page.locator('#inbox-workspace').textContent()).includes(results.get('twilio-voice').referenceNumber) && (await page.locator('.call-taker-notice').textContent()).includes('Callback requested'));
    await scan('omnichannel-inbox-desktop');await capture('inbox-omnichannel');
    await page.locator('#inbox-channel-filter').selectOption('whatsapp');check('Channel filter isolates WhatsApp thread',await page.locator('[data-conversation]').count()===1);
    await openConversation(results.get('whatsapp').conversationId);check('WhatsApp selected thread preserves isiZulu language',await page.locator('#inbox-reply-language').inputValue()==='zu');check('Provider media is a non-clickable bounded reference with an honest privacy label',await page.locator('.channel-media-refs code').textContent()==='synthetic-provider-media-01' && (await page.locator('.channel-media-refs').textContent()).includes('NOT FETCHED') && await page.locator('.channel-media-refs a,.channel-media-refs img,.channel-media-refs video,.channel-media-refs audio').count()===0);
    check('Human reply is disabled before named approval and rationale',await page.locator('#inbox-send').isDisabled());
    await page.locator('#inbox-reply-template').selectOption('channel.notice');check('Human notice preview includes three WhatsApp interactive choices',await page.locator('#inbox-interactive-preview').isVisible() && await page.locator('.channel-menu-preview li').count()===3);
    await page.locator('#inbox-reply-template').selectOption('case.update');await page.locator('#inbox-reply-update').fill('SYNTHETIC: dispatcher reviewed the water leak report; no arrival time is promised.');await page.locator('#inbox-reply-reason').fill('Synthetic dispatcher reviewed the case and requested language.');check('Rationale alone cannot authorize a message',await page.locator('#inbox-send').isDisabled());
    await page.locator('#inbox-human-approve').check();check('Explicit human approval unlocks the simulated reply',await page.locator('#inbox-send').isEnabled());
    await page.locator('#inbox-refresh').click();await page.waitForFunction(()=>!CivicOpsChannels.diagnostics().loading);
    check('Unchanged refresh retains the composed reply and explicit approval',await page.locator('#inbox-human-approve').isChecked() && (await page.locator('#inbox-reply-update').inputValue()).includes('dispatcher reviewed the water leak'));
    await command('whatsapp','HELP');await page.locator('#inbox-refresh').click();await page.waitForFunction(()=>!CivicOpsChannels.diagnostics().loading);
    check('A changed conversation retains its draft and requires fresh human approval',!await page.locator('#inbox-human-approve').isChecked() && await page.locator('#inbox-send').isDisabled() && (await page.locator('#inbox-reply-reason').inputValue()).includes('Synthetic dispatcher'));
    await page.locator('#inbox-human-approve').check();
    const replyAttempts=[];let acceptedLostReply=false;
    const replyRoute='**/api/v1/channels/conversations/'+results.get('whatsapp').conversationId+'/reply';
    await page.route(replyRoute,async route=>{replyAttempts.push(JSON.parse(route.request().postData()));if(!acceptedLostReply){const response=await route.fetch();check('Synthetic reply is accepted before the transport response is lost',response.status()===200);acceptedLostReply=true;await route.abort('failed');}else await route.continue();});
    await page.locator('#inbox-send').click();await page.waitForFunction(()=>document.querySelector('#inbox-reply-result')?.classList.contains('error'));
    check('A lost response preserves human approval and a safe retry',await page.locator('#inbox-send').isEnabled() && await page.locator('#inbox-human-approve').isChecked());
    await page.locator('#inbox-send').click();await page.waitForFunction(()=>document.querySelector('#inbox-reply-result')?.textContent.includes('SIMULATED'));await page.unroute(replyRoute);
    check('Retry reuses the exact approved reply operation id',replyAttempts.length===2 && replyAttempts[0].operationId===replyAttempts[1].operationId);
    check('Human-approved outbound appears in the conversation with simulation and template provenance',await page.locator('.channel-message.outbound').count()===1 && (await page.locator('.channel-message.outbound').textContent()).includes('SIMULATED') && (await page.locator('.channel-message.outbound').textContent()).includes('case.update'));
    await page.locator('#inbox-timeline').evaluate(element=>element.scrollTop=element.scrollHeight);
    await scan('whatsapp-conversation-desktop');await capture('whatsapp-conversation',page.locator('#inbox-workspace'));
    await page.locator('#inbox-channel-filter').selectOption('');await page.locator('#inbox-queue-filter').check();check('Human queue filter includes the voice requests and resident SOS-info follow-up',await page.locator('[data-conversation]').count()===4);await page.locator('#inbox-queue-filter').uncheck();
    await page.locator('[data-view="integrations"]').click();await page.waitForSelector('#integrations-content:not([hidden])');check('Integrations expose readiness for each provider and three per-channel metric rows',await page.locator('.channel-provider').count()>=6 && await page.locator('#channel-metrics tr').count()>=3 && (await page.locator('#channel-external-mode').textContent()).includes('SIMULATED'));
    await scan('channel-integrations-desktop');
    await role('admin');await page.locator('[data-view="admin"]').click();await page.waitForSelector('#admin-content:not([hidden])');await page.waitForFunction(()=>document.querySelectorAll('.template-record').length===5);
    check('Admin template panel has five templates and all fifteen language versions',await page.locator('.template-record').count()===5 && await page.locator('.template-language').count()===15);
    await page.locator('#channel-template-key').selectOption('case.received');await page.locator('#channel-template-language').selectOption('af');check('Afrikaans preview carries its language and an explicit owner-review label',await page.locator('#channel-template-preview').getAttribute('lang')==='af' && /review|translation/i.test(await page.locator('#channel-template-review').textContent()));
    await page.locator('#channel-template-language').selectOption('en');const original=await page.locator('#channel-template-text').inputValue();const version=await page.locator('#channel-template-version').textContent();
    await page.locator('#channel-template-text').fill(original+' Synthetic municipal exercise.');check('Unsaved template preview uses safe synthetic variables',(await page.locator('#channel-template-preview').textContent()).includes('CIV-20261009-00000011') && (await page.locator('#channel-template-preview').textContent()).includes('Synthetic municipal exercise.'));
    await page.locator('#channel-template-reason').fill('Synthetic municipal administrator reviewed this template revision.');await page.locator('#channel-template-save').click();await page.waitForFunction(()=>document.querySelector('#channel-template-result')?.textContent.includes('saved as'));
    check('Saving increments template version and records an audit result',await page.locator('#channel-template-version').textContent()!==version && (await page.locator('#channel-template-result').textContent()).includes('audited'));
    await page.locator('#channel-template-server-preview').click();await page.waitForFunction(()=>document.querySelector('#channel-template-preview-state').textContent.startsWith('Saved server template'));
    check('Admin can preview the validated saved server version',(await page.locator('#channel-template-preview').textContent()).includes('Synthetic municipal exercise.'));
    await scan('templates-admin-desktop');await capture('templates-admin',page.locator('#admin-channel-templates'));
    await page.setViewportSize({width:390,height:844});await page.locator('[data-view="inbox"]').click();await page.waitForSelector('#inbox-content:not([hidden])');check('Inbox has no horizontal overflow on a phone',await noOverflow());await scan('omnichannel-inbox-phone');
    await page.setViewportSize({width:320,height:844});check('Inbox fits a 320-pixel viewport',await noOverflow());
    await page.emulateMedia({colorScheme:'dark'});await scan('omnichannel-inbox-dark-phone');
    await page.setViewportSize({width:1440,height:1120});await page.emulateMedia({colorScheme:'light'});
    const cookieWrite=await page.evaluate(async()=>{const response=await fetch('/api/v1/channels/conversations/synthetic-invalid/reply',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});return {status:response.status,body:await response.json()};});check('Cookie channel writes require CSRF',cookieWrite.status===400 && cookieWrite.body.title==='Request verification failed' && /security token/i.test(cookieWrite.body.error),JSON.stringify(cookieWrite));
    await role('auditor');await page.locator('[data-view="inbox"]').click();await page.waitForSelector('#inbox-content:not([hidden])');await openConversation(results.get('whatsapp').conversationId);check('Auditor can inspect but cannot approve a resident reply',await page.locator('#inbox-send').isDisabled() && (await page.locator('#inbox-send-state').textContent()).includes('dispatcher'));
    await role('supervisor','synthetic-coast');await page.goto(base+'/m/coast/#inbox',{waitUntil:'domcontentloaded'});await page.waitForSelector('#inbox-content:not([hidden])');
    check('Municipality path clears previous resident threads and uses an empty coastal inbox',await page.locator('[data-conversation]').count()===0 && await page.evaluate(()=>municipalityPath==='/m/coast' && state.auth.tenantId==='synthetic-coast'));
    const crossTenant=await browserApi('/m/coast/api/v1/channels/conversations/'+results.get('whatsapp').conversationId,undefined,'GET');check('Coastal staff cannot retrieve an inland conversation id',crossTenant.status===404);
    const coastalTemplates=await browserApi('/m/coast/api/v1/channels/templates',undefined,'GET');check('Template revisions remain local to their municipality',coastalTemplates.status===200 && coastalTemplates.data.find(item=>item.key==='case.received' && item.language==='en').version===1);
    await page.goto(base+'/#inbox',{waitUntil:'domcontentloaded'});await role('supervisor','default');await page.waitForFunction(()=>document.querySelectorAll('[data-conversation]').length===6);
    await role('resident');check('Resident role loses inbox and integration navigation',await page.locator('[data-view="inbox"]').isHidden() && await page.locator('[data-view="integrations"]').isHidden());
    check('Role transition clears purpose-limited conversation data',await page.locator('[data-conversation]').count()===0 && await page.locator('#inbox-content').isHidden());
    const denied=await browserApi('/api/v1/channels/conversations',undefined,'GET');check('Server denies resident thread access',denied.status===403);
    await role('supervisor');const audit=await browserApi('/api/audit',undefined,'GET');const verified=await browserApi('/api/audit/verify',undefined,'GET');check('Unified intake and human replies retain a valid case audit chain',audit.status===200 && verified.status===200 && verified.data.valid===true);
    for(const [provider,data] of results)check(provider+' case is represented in municipal audit',audit.data.some(entry=>entry.referenceNumber===data.referenceNumber));
    check('Channel intake and communication decisions are audited',audit.data.some(entry=>entry.action.includes('channel')));
    check('No browser runtime errors',errors.length===0,errors.join('; '));check('No CSP violations',csp.length===0,JSON.stringify(csp));check('No external requests or media/provider/tile fetches',outside.length===0,JSON.stringify(outside));
    for(const file of screenshots)check(path.basename(file)+' is a nonempty actual browser capture',(await stat(file)).size>15000);
    await writeFile(path.join(output,'result.json'),JSON.stringify({base,checks,accessibility,screenshots,errors,csp,outside,auditEntries:verified.data.entries},null,2));
    console.log('PASS: '+checks.length+' channel checks; '+accessibility.length+' axe scans; '+verified.data.entries+' verified audit entries; 0 external requests.');
  } finally {await browser.close();}
})();
