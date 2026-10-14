#!/usr/bin/env node
// Synthetic agency board and simulated camera feed evidence. Local-only; no camera, provider or tile request.
import assert from 'node:assert/strict';
import {mkdir,readFile,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

const base=process.env.CIVICOPS_BROWSER_BASE_URL || 'http://127.0.0.1:5088';
const origin=new URL(base);assert(['127.0.0.1','localhost','[::1]'].includes(origin.hostname),'Agency smoke is restricted to a local synthetic server.');
const modulePath=process.env.CIVICOPS_PLAYWRIGHT_MODULE;
const {chromium}=await import(modulePath?pathToFileURL(modulePath).href:'playwright-core');
const axe=await readFile(process.env.CIVICOPS_AXE_CORE_PATH || path.resolve(path.dirname(modulePath || 'node_modules/playwright-core/index.mjs'),'../axe-core/axe.min.js'),'utf8');
const output=path.join(process.env.TMPDIR || tmpdir(),'agency-browser-smoke');
const media=process.env.CIVICOPS_UPDATE_MEDIA==='1'?path.resolve('docs/media'):output;
await mkdir(output,{recursive:true});await mkdir(media,{recursive:true});
const browser=await chromium.launch({executablePath:process.env.CIVICOPS_CHROME_PATH || '/usr/bin/google-chrome',headless:true,args:['--no-sandbox','--disable-dev-shm-usage']});
const context=await browser.newContext({viewport:{width:1440,height:1120},locale:'en-ZA',timezoneId:'Africa/Johannesburg',reducedMotion:'reduce',serviceWorkers:'block'});
const page=await context.newPage();
const checks=[];const outside=[];const errors=[];const csp=[];const screenshots=[];
const check=(name,result,detail='')=>{assert(result,name+(detail?': '+detail:''));checks.push(name);};
await context.route('**/*',route=>{if(new URL(route.request().url()).origin===origin.origin)return route.continue();outside.push(route.request().url());return route.abort('blockedbyclient');});
await context.exposeBinding('__agencyCsp',(_,event)=>csp.push(event));
await context.addInitScript(()=>document.addEventListener('securitypolicyviolation',event=>window.__agencyCsp({directive:event.effectiveDirective,blocked:event.blockedURI})));
page.on('pageerror',error=>errors.push(error.message));
async function scan(name){await page.evaluate(axe);const report=await page.evaluate(()=>axe.run(document.querySelector('#agencies'),{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']}}));await writeFile(path.join(output,'axe-'+name+'.json'),JSON.stringify(report,null,2));check(name+' has zero WCAG2.2 A/AA violations',report.violations.length===0,JSON.stringify(report.violations.map(v=>v.id+': '+v.nodes.map(n=>n.target).join(' '))));}
async function capture(name,locator){await page.evaluate(async()=>{document.activeElement?.blur();scrollTo(0,0);await document.fonts.ready;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));});const file=path.join(media,name+'.png');await locator.screenshot({path:file,animations:'disabled'});screenshots.push(file);}
const idle=()=>page.waitForFunction(()=>!document.querySelector('#agency-list .empty-state')||true).then(()=>page.waitForTimeout(600));
try{
  await page.goto(base+'/',{waitUntil:'domcontentloaded'});await page.waitForFunction(()=>!!document.querySelector('#staff-access'));
  await page.locator('#staff-access').evaluate(e=>e.open=true);await page.locator('#demo-tenant option[value="default"]').waitFor({state:'attached'});
  await page.locator('#demo-role').selectOption('supervisor');await page.locator('#demo-tenant').selectOption('default');await page.locator('#demo-login-form button').click();
  await page.waitForFunction(()=>state.auth?.authenticated===true && /EXPLICIT DEMO supervisor/.test(state.auth.name||''),null,{timeout:15000});check('Demo sign-in produced the named supervisor session',true);
  await page.locator('[data-view="agencies"]').waitFor({state:'visible'});check('Agencies navigation is visible to operations staff',true);
  // Reload so the shell starts from the settled session and its current CSRF token.
  await page.goto(base+'/#agencies',{waitUntil:'domcontentloaded'});await page.reload({waitUntil:'domcontentloaded'});await page.waitForFunction(()=>state.auth?.authenticated===true && /EXPLICIT DEMO supervisor/.test(state.auth.name||'') && state.view==='agencies',null,{timeout:20000});
  await page.locator('#agency-content').waitFor({state:'visible',timeout:20000});
  await page.locator('#agency-seed').waitFor({state:'visible',timeout:15000});await page.locator('#agency-seed').click();await page.waitForFunction(()=>document.querySelectorAll('#agency-list [data-incident]').length>=2 && document.querySelectorAll('#camera-list [data-detection]').length>=2,null,{timeout:15000});
  check('Synthetic scenario opens cross-agency incidents',await page.locator('#agency-list [data-incident]').count()>=2);
  check('Camera feed is labelled simulated and scene-only',/SIMULATED/.test(await page.locator('.camera-card').textContent()) && /no imagery, faces, plates or identities/.test(await page.locator('#camera-notice').textContent()));
  check('Camera events are proposals awaiting a human',await page.locator('#camera-list [data-confirm="true"]').count()>=2);
  await page.locator('#agency-list [data-incident]').last().click();await idle();
  await page.locator('#agency-commander-form button[type="submit"]').click();await page.waitForFunction(()=>/Incident Commander/.test(document.querySelector('#agency-detail').textContent) && /commander assigned/.test(document.querySelector('#agency-detail').textContent),null,{timeout:10000});
  check('Supervisor assigns the incident commander and the timeline records it',true);
  await page.locator('#agency-detail [data-accept="true"]').first().click();await page.waitForFunction(()=>/Accepted by/.test(document.querySelector('#agency-detail').textContent),null,{timeout:10000});
  check('Supervisor decides mutual aid with a recorded decision',true);
  check('Timeline names the signed-in supervisor, not the anonymous demo fallback',/EXPLICIT DEMO supervisor/.test(await page.locator('.agency-timeline').textContent()) && !/not signed in/.test(await page.locator('.agency-timeline').textContent()));
  await capture('agency-board',page.locator('#agencies'));await scan('agency-board');
  const before=await page.locator('#agency-list [data-incident]').count();
  await page.locator('#camera-list [data-confirm="true"]').first().click();await page.waitForFunction(n=>document.querySelectorAll('#agency-list [data-incident]').length===n+1,before,{timeout:10000});
  check('Confirming a camera event opens a human-confirmed incident',/Human-confirmed camera event/.test(await page.locator('#agency-detail').textContent()));
  await page.locator('#camera-list [data-confirm="false"]').first().click();await page.waitForFunction(()=>/Dismissed/.test(document.querySelector('#camera-list').textContent),null,{timeout:10000});
  check('Dismissing a camera event records the decision without an incident',true);
  await capture('camera-feed',page.locator('.camera-card'));
  await page.setViewportSize({width:390,height:900});await page.waitForTimeout(300);
  check('Agency board fits a 390-pixel phone without horizontal scroll',await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1));
  check('No page errors, CSP violations or outside requests',errors.length===0 && csp.length===0 && outside.length===0,JSON.stringify({errors,csp,outside}));
  console.log(JSON.stringify({passed:checks.length,checks,screenshots},null,2));
} catch(error) { console.error('agency-error text:', await page.locator('#agency-error').textContent().catch(()=>'')); throw error; } finally { await browser.close(); }
