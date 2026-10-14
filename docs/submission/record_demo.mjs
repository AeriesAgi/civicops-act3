// Records the synthetic CivicOps walkthrough (local server, fully simulated). Writes raw/demo.webm and raw/marks.json.
import {writeFile, rename, readdir} from 'node:fs/promises';
const {chromium} = await import(process.env.PW);
const base = 'http://127.0.0.1:5099';
const out = process.env.CIVICOPS_RECORD_OUT || 'submission/raw';
const browser = await chromium.launch({executablePath:'/usr/bin/google-chrome', args:['--no-sandbox','--disable-dev-shm-usage']});
const context = await browser.newContext({viewport:{width:1440,height:900}, recordVideo:{dir:out, size:{width:1440,height:900}}, locale:'en-ZA', timezoneId:'Africa/Johannesburg', serviceWorkers:'block'});
await context.route('**/*', r => new URL(r.request().url()).origin === base ? r.continue() : r.abort());
const page = await context.newPage();
const t0 = Date.now(); const marks = [];
const mark = (text) => { marks.push({at:(Date.now()-t0)/1000, text}); console.log(((Date.now()-t0)/1000).toFixed(1), text); };
const wait = ms => page.waitForTimeout(ms);
const smooth = async (y, ms=1200) => { await page.evaluate(([y,ms]) => window.scrollTo({top:y, behavior:'smooth'}), [y,ms]); await wait(ms); };
const view = async v => { await page.evaluate(v => { location.hash = v; }, v); await wait(1500); await smooth(0, 300); };
try {
  await page.goto(base + '/'); await wait(1500);
  // Sign in through Staff access, then reload once (demo script).
  await page.locator('#staff-access').evaluate(e => e.open = true);
  await page.locator('#demo-tenant option[value="default"]').waitFor({state:'attached'});
  await page.locator('#demo-role').selectOption('supervisor'); await page.locator('#demo-login-form button').click(); await wait(1500);
  await page.reload(); await page.waitForFunction(()=>/EXPLICIT DEMO supervisor/.test(state.auth?.name||''),null,{timeout:15000}); await wait(1500);
  await page.locator('#staff-access').evaluate(e => e.open = false);
  mark('Resident desk: one front door for voice, chat, SMS and WhatsApp. Everything here is synthetic and simulated.');
  await wait(4500);
  await smooth(700, 1500);
  mark('A resident reports a blocked stormwater drain to the simulated agent. It asks what and where, and gives a reference.');
  await page.locator('#chat-area').fill('Synthetic Ward 6');
  if (await page.locator('#chat-privacy-consent').count()) await page.locator('#chat-privacy-consent').check().catch(()=>{});
  await page.locator('#chat-message').click();
  await page.locator('#chat-message').pressSequentially('The stormwater drain on Synthetic Main Rd is blocked and the road is flooding.', {delay:25});
  await page.locator('#chat-form button[type="submit"], #chat-form button').last().click(); await wait(5000);
  await view('control');
  mark('Control room: bounded agents triage and recommend. Nothing is dispatched without a human.');
  if (await page.locator('#seed').isVisible()) { await page.locator('#seed').click(); await wait(3500); }
  await wait(2500);
  await smooth(650, 1500);
  await page.locator('#queue button, #queue [data-id]').first().click().catch(()=>{}); await wait(2500);
  mark('Each case shows the agent runs, provider label (deterministic-fallback), confidence and the human-review gate.');
  await smooth(1000, 1500); await wait(3500);
  const approve = page.locator('#case-detail [data-action="approve"]');
  if (await approve.count()) { mark('The dispatcher approves the response, then assigns an eligible crew. Zendesk delivery is a simulated outbox.'); await approve.first().click(); await wait(3000);
    const assign = page.locator('#case-detail [data-action="assign"]'); if (await assign.count()) { await assign.first().click().catch(()=>{}); await wait(3000); } }
  await view('map');
  mark('Live map: synthetic cases, crews and hazard zones. Aggregated operations only, no person tracking or crime prediction.');
  if (await page.locator('#map-seed').isVisible()) { await page.locator('#map-seed').click(); }
  await wait(7000);
  await page.locator('#map-cases [data-map-case]').first().click().catch(()=>{}); await wait(5000);
  await view('agencies');
  mark('Agencies: Traffic, EMS, Fire and Electricity share one incident, one commander and one timeline.');
  await page.locator('#agency-seed').waitFor({state:'visible'}); await page.locator('#agency-seed').click();
  await page.waitForFunction(() => document.querySelectorAll('#agency-list [data-incident]').length >= 2, null, {timeout:15000}); await wait(2500);
  await page.locator('#agency-list [data-incident]').last().click(); await wait(2000);
  mark('A supervisor assigns the incident commander and decides the neighbouring metro\'s mutual-aid request.');
  await page.locator('#agency-commander-form button[type="submit"]').click(); await wait(2500);
  await page.locator('#agency-detail [data-accept="true"]').first().click(); await wait(3000);
  mark('Camera feed is SIMULATED and scene-only: no faces, no plates. A human confirms or dismisses every event.');
  await page.locator('#camera-list [data-confirm="true"]').first().click(); await wait(3500);
  await page.locator('#camera-list [data-confirm="false"]').first().click(); await wait(3000);
  await view('resident');
  mark('SOS: a deliberate 3-second hold alerts the municipal control room. It never replaces 10111, 10177 or 112.');
  await smooth(380, 1200);
  const hold = page.locator('#resident-sos-hold');
  await hold.dispatchEvent('pointerdown', {button:0, pointerType:'mouse'}); await wait(3600); await hold.dispatchEvent('pointerup', {button:0, pointerType:'mouse'}); await wait(2500);
  if (await page.locator('#resident-sos-address').isVisible()) { await page.locator('#resident-sos-address').fill('Synthetic training gate'); await page.locator('#resident-sos-address-submit').click(); }
  await wait(3500);
  await view('control');
  await page.locator('#sos-control').scrollIntoViewIfNeeded(); await wait(1500);
  mark('The control room acknowledges, dispatches or hands over, and resolves. Every step is audited with a named human.');
  await page.locator('#sos-queue button').first().click().catch(()=>{}); await wait(2000);
  for (let i = 0; i < 3; i++) { const b = page.locator('#sos-alert-actions [data-sos-action]').first(); if (await b.count()) { await b.click(); await wait(2500); } }
  await view('audit');
  mark('Audit: an append-only hash chain, verified locally. No AMD endpoint exists for this build; reasoning shown is deterministic and simulated.');
  await page.locator('#verify-audit').click(); await wait(6000);
  await view('control');
  mark('CivicOps: a conversational front door, accountable AI assistance, and a human decision at every dispatch, mutual-aid request and SOS.');
  await wait(7000);
  mark('END');
} finally {
  await writeFile(out + '/marks.json', JSON.stringify(marks, null, 2));
  await context.close(); await browser.close();
  for (const f of await readdir(out)) if (f.endsWith('.webm') && f !== 'demo.webm') await rename(out + '/' + f, out + '/demo.webm');
}
