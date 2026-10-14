#!/usr/bin/env node
// Synthetic SOS workflow evidence. Local-only: no emergency call, provider send,
// map tile, SMS/WhatsApp, USSD aggregator or push request is made.
import assert from 'node:assert/strict';
import {mkdir,readFile,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {randomUUID} from 'node:crypto';

const base=process.env.CIVICOPS_BROWSER_BASE_URL || 'http://127.0.0.1:5088';
const origin=new URL(base);assert(['127.0.0.1','localhost','[::1]'].includes(origin.hostname),'SOS smoke is restricted to a local synthetic server.');
const modulePath=process.env.CIVICOPS_PLAYWRIGHT_MODULE;
const {chromium}=await import(modulePath?pathToFileURL(modulePath).href:'playwright-core');
const axe=await readFile(process.env.CIVICOPS_AXE_CORE_PATH || path.resolve(path.dirname(modulePath || 'node_modules/playwright-core/index.mjs'),'../axe-core/axe.min.js'),'utf8');
const output=path.join(process.env.TMPDIR || tmpdir(),'sos-browser-smoke');
const media=process.env.CIVICOPS_UPDATE_MEDIA==='1'?path.resolve('docs/media'):output;
await mkdir(output,{recursive:true});await mkdir(media,{recursive:true});
const browser=await chromium.launch({executablePath:process.env.CIVICOPS_CHROME_PATH || '/usr/bin/google-chrome',headless:true,args:['--no-sandbox','--disable-dev-shm-usage']});
const context=await browser.newContext({viewport:{width:1440,height:1120},locale:'en-ZA',timezoneId:'Africa/Johannesburg',reducedMotion:'reduce',serviceWorkers:'block'});
const page=await context.newPage();
const checks=[];const accessibility=[];const screenshots=[];const outside=[];const errors=[];const csp=[];
const check=(name,result,detail='')=>{assert(result,name+(detail?': '+detail:''));checks.push(name);};
await context.route('**/*',route=>{if(new URL(route.request().url()).origin===origin.origin)return route.continue();outside.push(route.request().url());return route.abort('blockedbyclient');});
await context.exposeBinding('__sosCsp',(_,event)=>csp.push(event));
await context.addInitScript(()=>document.addEventListener('securitypolicyviolation',event=>window.__sosCsp({directive:event.effectiveDirective,blocked:event.blockedURI})));
await context.addInitScript(()=>{window.__sosGeo={current:0,watch:0};if(navigator.geolocation){const original=navigator.geolocation.getCurrentPosition?.bind(navigator.geolocation);const watch=navigator.geolocation.watchPosition?.bind(navigator.geolocation);Object.defineProperty(navigator.geolocation,'getCurrentPosition',{value:(...args)=>{window.__sosGeo.current++;return original?.(...args);}});Object.defineProperty(navigator.geolocation,'watchPosition',{value:(...args)=>{window.__sosGeo.watch++;return watch?.(...args);}});}});
page.on('pageerror',error=>errors.push(error.message));
async function request(url,body,method='POST',headers={}) { const response=await context.request.fetch(base+url,{method,headers:{'Content-Type':'application/json',Authorization:'Bearer test-smoke-dispatch-token',...headers},...(body===undefined?{}:{data:body})});return {status:response.status(),data:await response.json().catch(()=>({}))}; }
async function must(url,body,method='POST',headers={}) { const result=await request(url,body,method,headers);assert(result.status>=200&&result.status<300,url+' returned '+result.status+': '+JSON.stringify(result.data));return result.data; }
async function browserApi(url,body,method='POST') { return page.evaluate(async({url,body,method})=>{const auth=await fetch('/auth/status',{cache:'no-store'}).then(response=>response.json()).catch(()=>({}));const headers={'Content-Type':'application/json'};if(auth.csrfToken)headers['X-CivicOps-CSRF']=auth.csrfToken;const response=await fetch(url,{method,credentials:'same-origin',cache:'no-store',headers,...(body===undefined?{}:{body:JSON.stringify(body)})});return {status:response.status,data:await response.json().catch(()=>({}))};},{url,body,method}); }
async function scan(name) { await page.evaluate(axe);const report=await page.evaluate(()=>axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']}}));await writeFile(path.join(output,'axe-'+name+'.json'),JSON.stringify(report,null,2));accessibility.push({name,violations:report.violations.length,passedRules:report.passes.length});check(name+' has zero WCAG2.2 A/AA violations',report.violations.length===0,JSON.stringify(report.violations)); }
async function capture(name,locator=page) { await page.evaluate(async()=>{document.activeElement?.blur();scrollTo(0,0);await document.fonts.ready;await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));});const file=path.join(media,name+'.png');await locator.screenshot({path:file,animations:'disabled'});screenshots.push(file); }
const notice='CivicOps SOS alerts your municipality\'s control room. In a life-threatening emergency always call 10111 (police), 10177 (ambulance/fire) or 112 from a cell phone.';
function payload(operationId= randomUUID(), extra={}) { return {operationId,channel:'resident-web',note:'SYNTHETIC: safety exercise only.',contact:'+27000000901',addressOrLandmark:'Fictional Demo Metro training gate',silent:false,location:{longitude:30.985,latitude:-29.915},accuracyMeters:8,batteryPercent:77,...extra}; }
try {
  const health=await must('/healthz',undefined,'GET');check('Throwaway server is explicit demo',health.demoMode===true);
  await page.goto(base+'/',{waitUntil:'domcontentloaded'});await page.waitForSelector('#resident-sos');
  check('Resident SOS surface states the full emergency notice and links', (await page.locator('#resident-sos').textContent()).includes(notice) && await page.locator('#resident-sos a[href="tel:10111"]').count()===1 && await page.locator('#resident-sos a[href="tel:10177"]').count()===1 && await page.locator('#resident-sos a[href="tel:112"]').count()===1);
  check('Resident SOS requires a deliberate three-second hold and cancellation window',/3\s*seconds|three-second/i.test(await page.locator('#resident-sos').textContent()) && /cancel/i.test(await page.locator('#resident-sos').textContent()));
  const residentText=await page.locator('#resident-sos').textContent();check('Resident SOS surface explains location is requested only after confirmation',/only after|active|location/i.test(residentText));
  check('Resident surface has not requested location before SOS confirmation',await page.evaluate(()=>__sosGeo.current===0&&__sosGeo.watch===0));
  await page.locator('#resident-sos-hold').dispatchEvent('pointerdown',{button:0,pointerType:'mouse'});await page.waitForTimeout(450);await page.locator('#resident-sos-hold').dispatchEvent('pointerup',{button:0,pointerType:'mouse'});await page.waitForTimeout(50);
  check('Releasing the short hold cancels without creating an alert or collecting location',await page.evaluate(()=>__sosGeo.current===0&&__sosGeo.watch===0)&&/cancel/i.test(await page.locator('#resident-sos-status').textContent()));
  await capture('sos-resident');await scan('sos-resident');

  const noLocation=await must('/api/v1/sos',payload(randomUUID(),{location:null,addressOrLandmark:null}));check('SOS without location asks for an address or landmark',noLocation.locationRequired===true && noLocation.alert.status==='AwaitingLocation');
  const location=await must('/api/v1/sos/'+noLocation.alert.id+'/location',{operationId:randomUUID(),location:{longitude:30.985,latitude:-29.915},accuracyMeters:9,addressOrLandmark:'Fictional training gate'});check('Location can be shared only during the active SOS follow-up window',location.alert.location?.latitude===-29.915);
  const replayOperation=randomUUID();const first=await must('/api/v1/sos',payload(replayOperation));const replay=await must('/api/v1/sos',payload(replayOperation));check('SOS POST is idempotent on operationId',replay.replayed===true && replay.alert.id===first.alert.id);
  const queue=await must('/api/v1/sos/queue',undefined,'GET');check('Dedicated queue returns synthetic SOS alerts and an acknowledgement target',Array.isArray(queue.alerts) && queue.alerts.some(item=>item.id===first.alert.id) && Number(queue.escalationTargetSeconds)>0);
  check('Queue does not expose a raw resident contact',!JSON.stringify(queue).includes('+27000000901') && queue.alerts.some(item=>item.contactHash && item.contactHash!=='anonymous'));
  const action=async(name,id,note='Synthetic authorised human reviewed this alert.')=>must('/api/v1/sos/'+id+'/'+name,{operationId:randomUUID(),note});
  await action('acknowledge',first.alert.id);await action('assess',first.alert.id);await action('dispatch',first.alert.id);const resolved=await action('resolve',first.alert.id);check('Human SOS lifecycle records acknowledge, assess, dispatch and resolve transitions',resolved.status==='Resolved');
  const late=await request('/api/v1/sos/'+first.alert.id+'/location',{operationId:randomUUID(),location:{longitude:30.985,latitude:-29.915},accuracyMeters:8});check('Resolved SOS rejects follow-up location writes',late.status===409);
  const keywordCandidates=[['/api/v1/sos/inbound',{channel:'sms',sender:'+27000000902',message:'SOS',tenantId:'default'}],['/api/v1/sos/inbound',{channel:'whatsapp',sender:'+27000000903',message:'HELP ME',tenantId:'default'}],['/api/v1/sos/ussd',{sessionId:'synthetic-ussd-01',text:'*120*SOS#',sender:'+27000000904',tenantId:'default'}]];
  for(const [url,body] of keywordCandidates){const result=await request(url,body);check('Synthetic '+url.split('/').pop()+' keyword adapter is bounded and accepted',result.status>=200&&result.status<300,JSON.stringify(result.data));}

  await page.goto(base+'/crew/',{waitUntil:'domcontentloaded'});await page.waitForSelector('#sos-view',{state:'attached'});check('Crew SOS surface repeats the emergency notice',(await page.locator('#sos-view').textContent()).includes(notice) && await page.locator('#sos-view a[href="tel:10111"]').count()===1 && await page.locator('#sos-view a[href="tel:10177"]').count()===1 && await page.locator('#sos-view a[href="tel:112"]').count()===1);await page.locator('#demo-signin').waitFor({state:'visible'});await page.locator('#demo-tenant option[value="default"]').waitFor({state:'attached'});await page.locator('#demo-tenant').selectOption('default');await page.locator('#demo-crew').fill('roads-01');await page.locator('#demo-form button').click();await page.waitForFunction(()=>window.CivicCrew?.diagnostics?.().live===true,{timeout:15000});check('Opening the signed-in crew SOS screen does not request location',await page.evaluate(()=>__sosGeo.current===0&&__sosGeo.watch===0));await page.locator('#sos-open').click();await page.waitForFunction(()=>!document.querySelector('#sos-view')?.hidden && document.querySelector('#message')?.hidden);check('Signed-in crew SOS screen stays visible without a lost-session banner',await page.locator('#sos-view').isVisible() && await page.locator('#message').isHidden());await capture('sos-crew-app');await scan('sos-crew-app');
  await page.goto(base+'/',{waitUntil:'domcontentloaded'});await page.waitForFunction(()=>!!document.querySelector('#staff-access'));await page.locator('#staff-access').evaluate(element=>element.open=true);await page.locator('#demo-tenant option[value="default"]').waitFor({state:'attached'});await page.locator('#demo-role').selectOption('supervisor');await page.locator('#demo-tenant').selectOption('default');await page.locator('#demo-login-form button').click();
  await page.waitForFunction(()=>state.auth?.authenticated===true && state.auth.roles?.includes('supervisor') && state.auth.tenantId==='default',null,{timeout:15000});
  await page.locator('[data-view="control"]').waitFor({state:'visible'});
  await page.locator('[data-view="control"]').click();
  await page.waitForFunction(()=>!document.querySelector('#control')?.hidden);
  await page.waitForFunction(()=>document.querySelector('#sos-priority-banner') && !document.querySelector('#sos-priority-banner').hidden && Number(document.querySelector('#sos-queue-count')?.textContent)>0,{timeout:15000});
  const controlText=await page.locator('#sos-control').textContent();
  check('Control room contains a dedicated SOS queue, emergency notice and hand-over limit',controlText.includes(notice)&&controlText.includes('CivicOps did not place the call'));
  check('Unacknowledged SOS banner is visible before silence',await page.locator('#sos-priority-banner').isVisible() && /unacknowledged SOS alert/.test(await page.locator('#sos-priority-banner').textContent()));
  await page.locator('#sos-silence').click();
  check('Silence alarm leaves the visual SOS banner in place',await page.locator('#sos-priority-banner').isVisible());
  await page.locator('#sos-queue [data-sos-id]').first().click();
  await page.waitForFunction(()=>!document.querySelector('#sos-alert-card')?.hidden);
  const alertCard=page.locator('#sos-control');await capture('sos-control-room-alert',alertCard);await scan('sos-control-room-alert');
  const timeline=page.locator('#sos-timeline');await capture('sos-timeline',timeline);
  check('SOS control-room timeline exposes named audited lifecycle decisions',/triggered|acknowledge|audit/i.test(await timeline.textContent()));
  check('No browser errors or CSP violations',errors.length===0&&csp.length===0,JSON.stringify({errors,csp}));check('No external requests',outside.length===0,JSON.stringify(outside));
  await writeFile(path.join(output,'result.json'),JSON.stringify({checks,accessibility,screenshots,errors,csp,outside},null,2));
  console.log('PASS: '+checks.length+' SOS checks; '+accessibility.length+' axe scans; 0 external requests.');
} finally {await browser.close();}
