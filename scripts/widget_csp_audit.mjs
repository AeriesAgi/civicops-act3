#!/usr/bin/env node
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

const target = new URL(process.env.CIVICOPS_BROWSER_BASE_URL);
assert(['127.0.0.1','localhost','[::1]'].includes(target.hostname), 'Widget CSP smoke is local only.');
const checks=[];
const check=(name,condition)=>{assert(condition,name);checks.push(name);};
const bundle = await readFile(process.argv[2]);
const integrity = 'sha384-Hq6Ry47vrwXsbkoZrxzEYn2bHG8p+KtLso8+Tlz2FjHptRWI0FTgAKQoh25jF4Ms';
check('Pinned npm widget bundle integrity','sha384-' + createHash('sha384').update(bundle).digest('base64') === integrity);
const modulePath = process.env.CIVICOPS_PLAYWRIGHT_MODULE;
const { chromium } = await import(modulePath ? pathToFileURL(modulePath).href : 'playwright-core');
const browser = await chromium.launch({ executablePath:process.env.CIVICOPS_CHROME_PATH || '/usr/bin/google-chrome',
  headless:true, args:['--no-sandbox','--disable-dev-shm-usage'] });
const context = await browser.newContext({serviceWorkers:'block'});
const violations = [], errors = [], intercepted = [], unexpected = [];
await context.exposeBinding('__recordWidgetCsp', (_, violation) => violations.push(violation));
await context.addInitScript(() => document.addEventListener('securitypolicyviolation', event =>
  window.__recordWidgetCsp({directive:event.effectiveDirective, blocked:event.blockedURI})));
const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
await context.route('**/*', route => {
  const url = new URL(route.request().url());
  // This smoke checks the actual widget rendering, not session authentication. HTTPS staff sessions are tested separately.
  if(url.origin === target.origin && url.pathname === '/auth/status') return route.fulfill({contentType:'application/json',
    body:JSON.stringify({authenticated:false,demoMode:false,roles:[],permissions:[],csrfToken:'synthetic-no-writes'})});
  if(url.origin === target.origin) return route.continue();
  intercepted.push(url.origin + url.pathname);
  const cors = {'access-control-allow-origin':'*'};
  if(url.href === 'https://unpkg.com/@elevenlabs/convai-widget-embed@0.19.0/dist/index.js')
    return route.fulfill({contentType:'text/javascript',body:bundle,headers:cors});
  if(url.hostname === 'api.elevenlabs.io' && url.pathname === '/v1/convai/agents/synthetic-widget/widget')
    return route.fulfill({contentType:'application/json',headers:cors,body:JSON.stringify({widget_config:{
      variant:'full', language:'en', supported_languages:['en'], dismissible:true, text_input_enabled:true,
      transcript_enabled:true, placement:'bottom-right', avatar:{type:'orb',color_1:'#46c896',color_2:'#72bfff'},
      terms_html:'Synthetic widget privacy terms.'}})});
  if(url.hostname === 'fonts.googleapis.com') return route.fulfill({contentType:'text/css',body:'',headers:cors});
  if(url.hostname === 'storage.googleapis.com') return route.fulfill({contentType:'image/svg+xml',headers:cors,
    body:'<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"/>'});
  unexpected.push(url.origin + url.pathname);
  return route.abort('blockedbyclient');
});
try {
  const navigation = await page.goto(target.href,{waitUntil:'domcontentloaded'});
  const policy = navigation.headers()['content-security-policy'];
  check('Strict configured widget CSP',policy.includes("'nonce-") && !/unsafe-inline|unsafe-eval/.test(policy));
  await page.waitForFunction(() => document.querySelector('elevenlabs-convai')?.shadowRoot?.querySelector('style')?.textContent?.length > 10000,
    undefined,{timeout:15000});
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const rendering = await page.evaluate(() => {
    const widget = document.querySelector('elevenlabs-convai');
    const style = widget.shadowRoot.querySelector('style');
    const script = document.querySelector('script[src*="convai-widget-embed@0.19.0"]');
    return {nonce:style.nonce,styleLength:style.textContent.length,buttons:widget.shadowRoot.querySelectorAll('button').length,
      integrity:script.integrity,serverLocation:widget.getAttribute('server-location'),telemetryDisabled:window.__fpjs_d_m===true};
  });
  check('Widget style nonce matches CSP',rendering.nonce && policy.includes("'nonce-" + rendering.nonce + "'"));
  check('Actual pinned widget rendered controls and theme',rendering.buttons >= 3 && rendering.styleLength > 10000);
  check('Widget pin, configured region and disabled third-party monitoring',rendering.integrity===integrity && rendering.serverLocation==='global' && rendering.telemetryDisabled);
  check('No widget CSP violations',violations.length===0); check('No widget runtime errors',errors.length===0);
  check('Every provider/dependency request was expected and intercepted',unexpected.length===0);
  console.log(JSON.stringify({passed:checks.length,checks,pinnedWidget:'0.19.0',renderedButtons:rendering.buttons,themeCharacters:rendering.styleLength,
    cspViolations:violations.length,runtimeErrors:errors.length,providerRequestsSent:0,intercepted},null,2));
} catch(error) {
  console.error(JSON.stringify({violations,errors,unexpected,intercepted},null,2)); throw error;
} finally { await context.close(); await browser.close(); }
