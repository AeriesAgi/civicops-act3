#!/usr/bin/env node
// Offline browser evidence against a throwaway synthetic server. No provider or tile requests leave this browser.
import assert from 'node:assert/strict';
import {mkdir,readFile,writeFile,stat} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

const baseUrl=process.env.CIVICOPS_BROWSER_BASE_URL || 'http://127.0.0.1:5088';
const origin=new URL(baseUrl);
assert(['127.0.0.1','localhost','[::1]'].includes(origin.hostname),'Map smoke requires a local synthetic server.');
const modulePath=process.env.CIVICOPS_PLAYWRIGHT_MODULE;
const {chromium}=await import(modulePath ? pathToFileURL(modulePath).href : 'playwright-core');
const axePath=process.env.CIVICOPS_AXE_CORE_PATH || (modulePath ? path.resolve(path.dirname(modulePath),'../axe-core/axe.min.js') : path.resolve('node_modules/axe-core/axe.min.js'));
const axe=await readFile(axePath,'utf8');
const output=path.join(process.env.TMPDIR || tmpdir(),'map-browser-smoke');
const media=process.env.CIVICOPS_UPDATE_MEDIA==='1' ? path.resolve('docs/media') : output;
await mkdir(output,{recursive:true});await mkdir(media,{recursive:true});
const browser=await chromium.launch({executablePath:process.env.CIVICOPS_CHROME_PATH || '/usr/bin/google-chrome',headless:true,args:['--no-sandbox','--disable-dev-shm-usage']});
const context=await browser.newContext({viewport:{width:1680,height:1120},deviceScaleFactor:1,locale:'en-ZA',timezoneId:'Africa/Johannesburg',colorScheme:'dark',reducedMotion:'reduce',serviceWorkers:'block'});
const checks=[];const errors=[];const csp=[];const outside=[];const screenshots=[];const accessibility=[];
const check=(name,condition,detail='')=>{assert(condition,detail ? name+': '+detail : name);checks.push(name);};
await context.exposeBinding('__mapCsp',(_,event)=>csp.push(event));
await context.addInitScript(()=>{
  document.addEventListener('securitypolicyviolation',event=>window.__mapCsp({directive:event.effectiveDirective,blocked:event.blockedURI}));
  window.__mapLocationCalls=0;
  for(const name of ['getCurrentPosition','watchPosition']) Object.defineProperty(navigator.geolocation,name,{value:()=>{window.__mapLocationCalls++;throw new Error('Map must never collect worker or resident location implicitly.');}});
});
await context.route('**/*',route=>{const url=new URL(route.request().url());if(url.origin===origin.origin) return route.continue();outside.push(url.origin);return route.abort('blockedbyclient');});
const page=await context.newPage();page.on('pageerror',failure=>errors.push(failure.message));
const waitText=(selector,text)=>page.waitForFunction(({selector,text})=>document.querySelector(selector)?.textContent.includes(text),{selector,text});
const frame=()=>page.evaluate(async()=>{await document.fonts.ready;await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));});
async function shot(name,locator=page.locator('#map'),maximumHeight=null) {
  await frame();const file=path.join(media,name+'.png');let cropStyle=null;const cropUrl=baseUrl+'/__civicops_map_capture.css';
  try {if(maximumHeight) {await page.route(cropUrl,route=>route.fulfill({contentType:'text/css',body:'#case-detail {max-height:'+maximumHeight+'px;overflow:hidden;}'}));cropStyle=await page.addStyleTag({url:cropUrl});}await locator.screenshot({path:file,animations:'disabled'});screenshots.push(file);}
  finally {if(cropStyle){await cropStyle.evaluate(element=>element.remove());await page.unroute(cropUrl);}}
}
async function apiJson(url,options={}) {return page.evaluate(async({url,options})=>api(url,options,true),{url,options});}
async function axeScan(name) {
  await page.waitForTimeout(300);await resetView();await page.locator('#map-feature-browser').evaluate(element=>{element.open=true;});await page.locator('#map-accessible-filter').selectOption('');await frame();await page.evaluate(axe);
  await page.locator('#ops-map').scrollIntoViewIfNeeded();await frame();
  check(name+' scans visible interactive geographic markers',await page.locator('#ops-map .leaflet-marker-icon[role="button"]').evaluateAll(elements=>elements.some(element=>{const rect=element.getBoundingClientRect();return rect.width>0 && rect.height>0 && rect.bottom>0 && rect.top<innerHeight && rect.right>0 && rect.left<innerWidth;})));
  const {report,equivalentEvidence,nodeEvidence}=await page.evaluate(async()=> {
    const native=await window.axe.run(document,{elementRef:true,runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']}});
    const controls=[...document.querySelectorAll('#map-accessible-items [data-map-feature]')].map(element=>{const rect=element.getBoundingClientRect();return {id:element.dataset.mapFeature,width:rect.width,height:rect.height,name:element.textContent.trim(),keyboard:element.tabIndex===0};});
    const evidence=native.violations.map(violation=>violation.nodes.map(node=> {
      const element=node.element;
      if(violation.id!=='target-size' || !element?.matches('.leaflet-marker-icon[data-map-essential="geographic-pin"]') || element.getAttribute('role')!=='button')return null;
      const ids=element.dataset.mapEquivalent ? [element.dataset.mapEquivalent] : element.dataset.mapClusterMembers?.split(',') || [];
      if(!ids.length)return null;
      const matches=ids.map(id=>controls.find(control=>control.id===id));
      return matches.every(item=>item && item.width>=24 && item.height>=24 && item.keyboard && item.name) ? {featureIds:ids,equivalentTargets:matches.length,essential:element.dataset.mapEquivalent ? 'Geographic marker remains at its actual coordinate; its equivalent keyboard control was measured with the native axe element reference.' : 'Spatial aggregate of underlying incident coordinates; every underlying incident has an equivalent keyboard control measured with the native axe element reference.'} : null;
    }));
    // Serialize the complete native result without filtering any rule or violation. DOM references serialize as empty objects.
    return {report:JSON.parse(JSON.stringify(native)),equivalentEvidence:controls,nodeEvidence:evidence};
  });
  check(name+' complete equivalent map controls have names, keyboard focus and at least 24px targets',equivalentEvidence.length===167 && equivalentEvidence.every(item=>item.name && item.keyboard && item.width>=24 && item.height>=24));
  const exceptions=[];const other=[];
  report.violations.forEach((violation,violationIndex)=>violation.nodes.forEach((node,nodeIndex)=> {
    const evidence=nodeEvidence[violationIndex][nodeIndex];
    if(evidence)exceptions.push({rule:violation.id,target:node.target,...evidence});else other.push({id:violation.id,target:node.target,summary:node.failureSummary});
  }));
  const file=path.join(output,'axe-map-'+name+'.json');await writeFile(file,JSON.stringify(report,null,2));
  const evidenceFile=path.join(output,'axe-map-'+name+'-exceptions.json');await writeFile(evidenceFile,JSON.stringify({source:'https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html',exceptions,equivalentEvidence,otherViolations:other},null,2));
  accessibility.push({name,nativeViolationRules:report.violations.length,acceptedGeographicPinExceptions:exceptions.length,otherViolations:other.length,passedRules:report.passes.length,file,evidenceFile});
  check(name+' map has zero unexcepted WCAG A/AA violations',other.length===0,JSON.stringify(other));await page.locator('#map-feature-browser').evaluate(element=>{element.open=false;});
}
async function resetView() {await page.locator('#map-reset-view').click();await page.waitForFunction(()=>document.querySelector('#map-count-cases').textContent==='120');await page.waitForTimeout(550);await frame();}
async function verifyInspectorKeyboardScroll() {
  // System font metrics can make the snapshot overflow on a stock runner. Exercise actual
  // keyboard scrolling at a constrained height so this guard does not depend on installed fonts.
  const stylesheet=baseUrl+'/__civicops_map_keyboard_scroll.css';
  await page.route(stylesheet,route=>route.fulfill({contentType:'text/css',body:'#map-inspector { max-height:180px; }'}));
  let style;
  try {
    style=await page.addStyleTag({url:stylesheet});await frame();
    const inspector=page.locator('#map-inspector');
    check('Snapshot inspector exercises overflowing content across system fonts',await inspector.evaluate(element=>element.scrollHeight>element.clientHeight));
    await inspector.evaluate(element=> {
      element.scrollTop=0;
      const preceding=[...document.querySelectorAll('button,a[href],input,select,textarea,summary,[tabindex]')].filter(candidate=>
        candidate.tabIndex>=0 && !candidate.disabled && candidate.getClientRects().length && getComputedStyle(candidate).visibility!=='hidden'
        && (candidate.compareDocumentPosition(element)&Node.DOCUMENT_POSITION_FOLLOWING)).at(-1);
      if(!preceding)throw new Error('Map inspector must have a preceding keyboard control.');
      preceding.focus();
    });
    await page.keyboard.press('Tab');
    check('Overflowing snapshot inspector is reached by Tab with visible keyboard focus',await inspector.evaluate(element=> {
      const computed=getComputedStyle(element);
      return document.activeElement===element && computed.outlineStyle!=='none' && parseFloat(computed.outlineWidth)>=2;
    }));
    await page.keyboard.press('ArrowDown');
    await page.waitForFunction(()=>document.querySelector('#map-inspector').scrollTop>0,null,{timeout:3000});
    check('Keyboard users can scroll the snapshot inspector with ArrowDown',await inspector.evaluate(element=>element.scrollTop>0));
  } finally {
    if(style)await style.evaluate(element=>element.remove());
    await page.unroute(stylesheet);await frame();
  }
}
try {
  const health=await context.request.get(baseUrl+'/healthz');const healthBody=await health.json();check('Map smoke uses explicit demo with an email outbox',healthBody.demoMode===true && healthBody.emailMode==='outbox');
  await page.goto(baseUrl+'/#map',{waitUntil:'domcontentloaded'});
  await page.waitForSelector('#map-content:not([hidden])');await page.waitForSelector('#ops-map.leaflet-container');
  check('Pinned Leaflet and clustering are loaded from the same origin',await page.evaluate(()=>L.version==='1.9.4' && typeof L.markerClusterGroup==='function'));
  const config=await apiJson('/api/v1/map/config');check('Baseline map renders without a tile dependency',config.tileUrl==='none' && config.tileEnabled===false);
  await page.locator('#map-seed').click();await page.waitForFunction(()=>document.querySelector('#map-count-cases').textContent==='95');
  await page.locator('#map-window').selectOption('7d');await page.waitForFunction(()=>document.querySelector('#map-count-cases').textContent==='120');
  const layers=await apiJson('/api/v1/map/layers?layers=cases,crews,hazards,wards,municipalities,assets&limit=500');
  check('Evening scenario contains 120 cases, 25 crews, 10 hazards and six synthetic wards',['cases','crews','hazards','wards'].map(layer=>layers.features.filter(feature=>feature.properties.layer===layer).length).join(',')==='120,25,10,6');
  check('Map labels distinguish synthetic boundaries and simulated positions',(await page.locator('#map-geography-badge').textContent()).includes('SYNTHETIC') && (await page.locator('.map-head-actions').textContent()).includes('SIMULATED'));
  check('Outline fallback contains ward polygons and a styled map background',await page.locator('#ops-map path.civic-ward').count()>=6 && (await page.locator('#map-outline-label').textContent()).includes('disabled') && await page.locator('#ops-map').evaluate(element=>getComputedStyle(element).backgroundImage.includes('gradient')));
  check('Screen-reader summary states cases, crews, hazards and human approval',(await page.locator('#map-summary').textContent()).includes('120 cases') && (await page.locator('#map-summary').textContent()).includes('25 crews') && (await page.locator('#map-summary').textContent()).includes('human approval'));
  check('Crew positions show visible status rings and stale ages',await page.locator('.map-crew-symbol').count()===25 && await page.locator('.map-crew-icon.stale').count()>0);
  await waitText('#map-stream-status','connected');check('Authenticated map SSE connects with a visible status',await page.locator('#map-sync').evaluate(element=>element.classList.contains('connected')));
  check('Open-case count is visible as a numeric snapshot',await page.locator('#map-open-count').evaluate(element=>/^\d+$/.test(element.textContent) && getComputedStyle(element).visibility==='visible' && element.getBoundingClientRect().height>=24));await shot('map-control-room');
  const initialHazards=await page.locator('.map-hazard-icon').count();await page.locator('#map-layer-hazards').uncheck();check('Hazard layer switch hides symbols and radii',initialHazards===10 && await page.locator('.map-hazard-icon').count()===0);await page.locator('#map-layer-hazards').check();
  await page.locator('#map-layer-crews').uncheck();check('Crew layer switch hides all position markers',await page.locator('.map-crew-icon').count()===0);await page.locator('#map-layer-crews').check();
  await page.locator('#map-layer-assets').uncheck();check('Asset layer switch hides infrastructure symbols',await page.locator('.map-asset-icon').count()===0);await page.locator('#map-layer-assets').check();
  const initialCaseMarkers=await page.locator('.map-case-icon').count();const initialScale=await page.locator('.leaflet-control-scale-line').first().textContent();check('Control-room map clusters cases at municipal scale',await page.locator('#ops-map .marker-cluster').count()>0);
  const clearCluster=await page.locator('#ops-map .marker-cluster').evaluateAll(elements=>elements.findIndex(element=>{const rect=element.getBoundingClientRect();const hit=document.elementFromPoint(rect.left+rect.width/2,rect.top+rect.height/2);return element===hit || element.contains(hit);}));check('A cluster can be reached by a physical pointer with all layers visible',clearCluster>=0);await page.locator('#ops-map .marker-cluster').nth(clearCluster).click();await page.waitForTimeout(750);check('Selecting a cluster expands its incident locations',await page.locator('.map-case-icon').count()>initialCaseMarkers || (await page.locator('.leaflet-control-scale-line').first().textContent())!==initialScale);
  await resetView();const keyboardScale=await page.locator('.leaflet-control-scale-line').first().textContent();const keyboardClusterName=await page.locator('#ops-map .marker-cluster').first().getAttribute('aria-label');await page.locator('#ops-map .marker-cluster').first().focus();await page.keyboard.press('Enter');await page.waitForTimeout(500);check('Cluster expansion is keyboard accessible with a descriptive name',(await page.locator('.leaflet-control-scale-line').first().textContent())!==keyboardScale && keyboardClusterName.includes('incident locations'));await resetView();const clearCrew=await page.locator('.map-crew-icon').evaluateAll(elements=>elements.findIndex(element=>{const rect=element.getBoundingClientRect();const hit=document.elementFromPoint(rect.left+rect.width/2,rect.top+rect.height/2);return element===hit || element.contains(hit);}));check('A crew marker can be reached by a physical pointer with all layers visible',clearCrew>=0);await page.locator('.map-crew-icon').nth(clearCrew).click();await waitText('#map-inspector','Location privacy');check('Crew marker opens detail with a last-seen age and privacy explanation',(await page.locator('#map-inspector').textContent()).includes('Last seen') && (await page.locator('#map-inspector').textContent()).includes('Seeded simulator'));
  await page.locator('#map-crew-qualifications').click();await waitText('#map-crew-evidence','Certification evidence');check('Crew detail shows aggregate skills and certificate evidence',(await page.locator('#map-crew-evidence').textContent()).includes('valid until'));
  await page.locator('.map-hazard-icon').first().click();await waitText('#map-inspector','Object and situation events only');check('Hazard detail requires human assessment and rejects biometric/predictive policing scope',(await page.locator('#map-inspector').textContent()).includes('video frames are never stored'));
  await resetView();
  // Heatmap is an aggregate grid; inspect its response and capture the actual canvas rendering.
  await page.locator('#map-layer-heatmap').check();await page.waitForSelector('.civic-map-heat');
  const heat=await apiJson('/api/v1/map/heatmap?metric=cases&window=7d');check('Heatmap exposes aggregated cells without person or crew identities',heat.features.length>0 && heat.features.every(feature=>feature.properties.count>=1) && !/workerId|residentId|personId|contactEmail/.test(JSON.stringify(heat)));
  await page.waitForFunction(()=>{const canvas=document.querySelector('.civic-map-heat');if(!canvas)return false;const data=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data;for(let index=3;index<data.length;index+=4)if(data[index]>0)return true;return false;});check('Heatmap toggle renders a nonempty local canvas layer',true);
  for(const layer of ['cases','crews','hazards','assets'])await page.locator('#map-layer-'+layer).uncheck();check('Heat density view retains the full scenario and a metric legend',await page.locator('#map-count-cases').textContent()==='120' && await page.locator('#map-heat-legend').isVisible());await shot('map-heatmap');for(const layer of ['cases','crews','hazards','assets'])await page.locator('#map-layer-'+layer).check();await page.locator('#map-heat-metric').selectOption('hazards');await page.waitForTimeout(500);check('Heat metric switches to hazard density',await page.locator('.civic-map-heat').count()===1);await page.locator('#map-layer-heatmap').uncheck();check('Heatmap switch removes the canvas layer',await page.locator('.civic-map-heat').count()===0);
  await resetView();const ward=page.locator('#ops-map path.civic-ward[tabindex="0"]').first();await ward.focus();await page.keyboard.press('Enter');check('Ward statistics are keyboard accessible',(await page.locator('#map-ward-info').textContent()).includes('open cases') && await page.locator('#map-ward-info').isVisible());
  await ward.evaluate(element=>element.dispatchEvent(new MouseEvent('mouseover',{bubbles:true})));await shot('map-ward-hover');await page.keyboard.press('Escape');check('Escape dismisses the ward hover summary',await page.locator('#map-ward-info').isHidden());
  const cases=layers.features.filter(feature=>feature.properties.layer==='cases');const pending=cases.find(feature=>feature.properties.status==='PendingApproval');
  await page.locator('#map-case-search').fill(pending.properties.referenceNumber);await page.locator('#map-search button').click();await waitText('#map-nearest','Review case before dispatch');
  check('Exact case reference search opens the tenant-scoped case inspector',(await page.locator('#map-inspector').textContent()).includes(pending.properties.referenceNumber));
  check('Unapproved case cannot trigger map dispatch',await page.locator('#map-dispatch-form').count()===0 && await page.locator('#map-review-first').isVisible());
  await page.locator('#map-open-case').click();await page.waitForSelector('#case-location-map.leaflet-container');await page.waitForFunction(id=>state.selected?.id===id,pending.properties.id);check('Map case click-through opens the actual workspace and compact geographic context',await page.locator('#control').isVisible() && (await page.locator('#case-detail').textContent()).includes(pending.properties.referenceNumber) && await page.locator('#case-location-map .map-case-icon').count()===1);
  const detailCrop=page.locator('#case-detail');await shot('map-case-detail',detailCrop,850);
  await page.locator('[data-view="map"]').click();await page.waitForSelector('#map-content:not([hidden])');
  // Confirm a synthetic assignment with the named human session; the endpoint rechecks all qualifications.
  let approved=null;for(const candidate of cases.filter(feature=>feature.properties.status==='Approved')) { const nearest=await apiJson('/api/v1/map/cases/'+encodeURIComponent(candidate.properties.id)+'/nearest-crews');if(nearest.suggestions.length){approved=candidate;break;} }
  check('Synthetic scenario has a human-approved incident with a qualified nearby crew',!!approved);
  await page.locator('#map-case-search').fill(approved.properties.referenceNumber);await page.locator('#map-search button').click();await page.waitForSelector('#map-dispatch-form');
  check('Nearest qualified crew shows distance and qualification reasons',(await page.locator('#map-nearest').textContent()).includes('km') && (await page.locator('#map-nearest').textContent()).includes('certificates'));
  const dispatchNote='Synthetic browser exercise: qualified available crew selected after human review.';
  const confirmMapDispatch=async()=>{await page.locator('#map-dispatch-note').fill(dispatchNote);await page.locator('#map-dispatch-form button').click();try{await waitText('#map-decision-result','audited');}catch(failure){const message=await page.locator('#map-decision-result').textContent();if(!/This case changed|Refresh the suggestion/.test(message||''))throw failure;await page.locator('#map-case-search').fill(approved.properties.referenceNumber);await page.locator('#map-search button').click();await page.waitForSelector('#map-dispatch-form');await page.locator('#map-dispatch-note').fill(dispatchNote);await page.locator('#map-dispatch-form button').click();await waitText('#map-decision-result','audited');}};
  await confirmMapDispatch();
  const assigned=await apiJson('/api/dispatch/incidents/'+encodeURIComponent(approved.properties.id));check('Human map confirmation assigns the crew and records actor, reason and time',assigned.status==='Assigned' && assigned.audit.some(event=>event.action==='assigned') && assigned.audit.some(event=>event.detail.includes('Synthetic browser exercise')));
  await resetView();await verifyInspectorKeyboardScroll();await axeScan('dark-desktop');
  await page.setViewportSize({width:720,height:950});check('Map reflows at the desktop 200% zoom equivalent',await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
  await page.setViewportSize({width:320,height:1000});check('Map reflows without horizontal overflow at 320 pixels',await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));await axeScan('dark-mobile');
  await page.emulateMedia({colorScheme:'light',reducedMotion:'reduce'});await axeScan('light-mobile');await page.setViewportSize({width:1680,height:1120});await axeScan('light-desktop');
  await page.locator('#map-refresh').focus();await page.keyboard.press('Enter');check('Map refresh has visible keyboard focus',await page.locator('#map-refresh').evaluate(element=>document.activeElement===element && getComputedStyle(element).outlineStyle!=='none'));
  check('Live map never implicitly requests a resident or worker position',await page.evaluate(()=>window.__mapLocationCalls===0));
  // Exercise tile failure using an intercepted local fake tile source; never contact a real tile API.
  const fallbackPage=await context.newPage();fallbackPage.on('pageerror',failure=>errors.push(failure.message));
  await fallbackPage.route('**/api/v1/map/config',async route=>{const result=await route.fetch();const body=await result.json();await route.fulfill({response:result,json:{...body,tileEnabled:true,tileUrl:baseUrl+'/__synthetic_tile_fail/{z}/{x}/{y}.png'}});});
  await fallbackPage.route('**/__synthetic_tile_fail/**',route=>route.fulfill({status:503,contentType:'text/plain',body:'Synthetic offline tile failure'}));
  await fallbackPage.goto(baseUrl+'/#map',{waitUntil:'domcontentloaded'});await fallbackPage.waitForFunction(()=>document.querySelector('#map-outline-label')?.textContent.includes('unavailable'));
  await fallbackPage.waitForFunction(()=>document.querySelectorAll('#ops-map path.civic-ward').length>=6 && document.querySelectorAll('#ops-map .leaflet-tile').length===0);
  check('Failed tile service preserves configured outlines and removes failed images',await fallbackPage.locator('#ops-map path.civic-ward').count()>=6 && await fallbackPage.locator('#ops-map .leaflet-tile').count()===0);await fallbackPage.close();
  await page.route('**/api/v1/map/layers?**',route=>route.fulfill({status:401,contentType:'application/json',body:JSON.stringify({error:'Synthetic expired-session guard'})}));
  await page.locator('#map-refresh').click();await page.waitForSelector('#map-content[hidden]',{state:'attached'});
  check('An expired map API authorization clears private points and recommendations',await page.locator('#ops-map .leaflet-marker-icon').count()===0 && await page.locator('#map-cases [data-map-case]').count()===0 && (await page.locator('#map-locked').textContent()).includes('cleared'));
  await page.unroute('**/api/v1/map/layers?**');await page.waitForFunction(()=>state.auth?.csrfToken);await page.locator('[data-view="map"]').click();await page.waitForFunction(()=>Number(document.querySelector('#map-count-cases').textContent)>0);
  await page.route('**/api/v1/map/stream',route=>route.fulfill({status:403,contentType:'application/json',body:JSON.stringify({error:'Synthetic revoked-stream guard'})}));
  await page.evaluate(async()=>CivicOpsMap.show());await page.waitForSelector('#map-content[hidden]',{state:'attached'});
  check('Revoked SSE authorization clears private map data and stops reconnecting',await page.locator('#ops-map .leaflet-marker-icon').count()===0 && await page.locator('#map-cases [data-map-case]').count()===0);
  await page.unroute('**/api/v1/map/stream');await page.waitForFunction(()=>state.auth?.csrfToken);
  await page.evaluate(async()=>staffTransition('/auth/demo-login',{role:'admin',tenantId:'synthetic-coast'},'Synthetic tenant isolation browser check.'));await page.waitForFunction(()=>document.querySelector('#map-count-cases').textContent==='0');
  check('Changing municipality clears previous cases, crew details and recommendations',await page.locator('#map-cases [data-map-case]').count()===0 && !(await page.locator('#map-inspector').textContent()).includes(approved.properties.referenceNumber));
  await page.evaluate(async()=>staffTransition('/auth/demo-login',{role:'resident',tenantId:'default'},'Synthetic resident authorization check.'));check('Resident role cannot view operational points',await page.locator('[data-view="map"]').isHidden() && await page.locator('#map-content').isHidden());
  check('All map flows produce no runtime JavaScript errors',errors.length===0,JSON.stringify(errors));check('All map flows preserve the strict CSP',csp.length===0,JSON.stringify(csp));check('No map or provider network request leaves the local test server',outside.length===0,JSON.stringify(outside));
  for(const file of screenshots) {const bytes=await readFile(file);check(path.basename(file)+' is a legible PNG below 1MB',bytes.readUInt32BE(16)>=500 && bytes.readUInt32BE(20)>=300 && (await stat(file)).size<1000000);}
  console.log(JSON.stringify({passed:checks.length,checks,screenshots,accessibility,artifacts:output,externalRequests:outside.length},null,2));
} catch(failure) {
  const diagnostic={passedBeforeFailure:checks.length,checks,error:failure.message,errors,csp,outside,inspector:await page.locator('#map-inspector').textContent().catch(()=>null),accessibility};
  await writeFile(path.join(output,'failure.json'),JSON.stringify(diagnostic,null,2));console.error(JSON.stringify(diagnostic,null,2));throw failure;
} finally {await context.close();await browser.close();}
