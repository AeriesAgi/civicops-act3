#!/usr/bin/env node
// Optional local browser QA. Uses an existing Chrome and existing Playwright module; downloads nothing.
import assert from 'node:assert/strict';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const baseUrl = process.env.CIVICOPS_BROWSER_BASE_URL || 'http://127.0.0.1:5088';
const target = new URL(baseUrl);
assert(['127.0.0.1', 'localhost', '[::1]'].includes(target.hostname), 'Browser smoke is restricted to a local simulated server.');
const modulePath = process.env.CIVICOPS_PLAYWRIGHT_MODULE;
const { chromium } = await import(modulePath ? pathToFileURL(modulePath).href : 'playwright-core');
const axePath = process.env.CIVICOPS_AXE_CORE_PATH || (modulePath ? path.resolve(path.dirname(modulePath), '../axe-core/axe.min.js') : path.resolve('node_modules/axe-core/axe.min.js'));
const axeSource = await readFile(axePath, 'utf8');
const accessibilityReports = [];
const outputDirectory = path.join(process.env.TMPDIR || tmpdir(), 'browser-smoke');
await mkdir(outputDirectory, { recursive: true });
const browser = await chromium.launch({
  executablePath: process.env.CIVICOPS_CHROME_PATH || '/usr/bin/google-chrome',
  headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage']
});
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1,
  locale: 'en-ZA', timezoneId: 'Africa/Johannesburg', colorScheme: 'light', reducedMotion: 'reduce', serviceWorkers: 'block' });
const cspViolations = [];
await context.exposeBinding('__recordCivicOpsCspViolation', (_, violation) => cspViolations.push(violation));
await context.addInitScript(() => {
  document.addEventListener('securitypolicyviolation', event => {
    window.__recordCivicOpsCspViolation({ directive: event.effectiveDirective, blocked: event.blockedURI });
  });
});
const page = await context.newPage();
const runtimeErrors = [];
page.on('pageerror', error => runtimeErrors.push(error.message));
await context.route('**/*', route => {
  const requestUrl = new URL(route.request().url());
  return requestUrl.origin === target.origin ? route.continue() : route.abort('blockedbyclient');
});
const checks = [];
// Routine QA never changes checked-in media. Explicit refresh publishes the affected enterprise captures.
const mediaDirectory = process.env.CIVICOPS_UPDATE_MEDIA === '1' ? path.resolve('docs/media') : outputDirectory;
const refreshedMedia = new Set(['admin-console','control-room','case-timeline']);
await mkdir(mediaDirectory, { recursive: true });
const screenshots = [];
async function stableFrame() {
  await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); });
}
async function shot(name, locator = page) {
  await stableFrame();
  const file = path.join(refreshedMedia.has(name) ? mediaDirectory : outputDirectory, name + '.png');
  await locator.screenshot({ path: file, animations: 'disabled' });
  screenshots.push(file);
}
async function axeScan(name) {
  await stableFrame();
  if(!await page.evaluate(() => !!window.axe)) await page.evaluate(axeSource);
  const report = await page.evaluate(async () => axe.run(document, {
    runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']}
  }));
  const contrastEvidence = await verifyFieldAndGradientContrast(name);
  const file = path.join(outputDirectory, 'axe-' + name + '.json');
  await writeFile(file,JSON.stringify({...report,contrastEvidence},null,2));
  accessibilityReports.push({name,violations:report.violations.length,passedRules:report.passes.length,incompleteRules:report.incomplete.map(item=>item.id),file});
  check(name + ' has zero axe WCAG 2.2 A/AA violations', report.violations.length === 0,
    JSON.stringify(report.violations.map(item => ({id:item.id,nodes:item.nodes.map(node=>({target:node.target,summary:node.failureSummary}))}))));
}
async function verifyFieldAndGradientContrast(name) {
  const evidence = await page.evaluate(() => {
    const channels = color => (color.match(/[\d.]+/g) || []).slice(0,3).map(Number).map(value => {
      const channel=value/255; return channel<=.04045 ? channel/12.92 : ((channel+.055)/1.055)**2.4;
    });
    const luminance = color => { const values=channels(color); return values[0]*.2126 + values[1]*.7152 + values[2]*.0722; };
    const ratio = (foreground,background) => { const a=luminance(foreground),b=luminance(background); return (Math.max(a,b)+.05)/(Math.min(a,b)+.05); };
    const samples=[];
    for(const element of document.querySelectorAll('input:not([type="checkbox"]),textarea,select,.table-wrap caption,#outcome-metrics .label,#outcome-metrics strong,#outcome-metrics small,.metrics-heading .eyebrow')) {
      if(!element.getClientRects().length || element.disabled) continue;
      const style=getComputedStyle(element); let background=style.backgroundColor;
      for(let parent=element;background==='rgba(0, 0, 0, 0)'&&parent;parent=parent.parentElement) background=getComputedStyle(parent).backgroundColor;
      let backgrounds=[background];
      const metric=element.closest('.metrics-card');
      if(metric) { const gradient=getComputedStyle(metric).backgroundImage; if(gradient!=='none') backgrounds=gradient.match(/rgb\([^)]+\)/g) || backgrounds; }
      const minimum=Math.min(...backgrounds.map(value=>ratio(style.color,value)));
      samples.push({selector:element.id || element.className,minimumRatio:Number(minimum.toFixed(2))});
    }
    return {samples,failures:samples.filter(sample=>sample.minimumRatio<4.5)};
  });
  check(name + ' form values and metric gradient text meet 4.5:1 contrast',evidence.failures.length===0,JSON.stringify(evidence.failures));
  return evidence;
}
async function focusAndTargets(view) {
  const problems = await page.evaluate(view => {
    const root = document.querySelector('#' + view);
    const visible = element => !!element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden';
    const controls = [...root.querySelectorAll('button,a[href],input,textarea,select,summary,[tabindex="0"]')].filter(element => visible(element) && !element.disabled);
    const errors=[];
    for(const element of controls) {
      const rect=element.getBoundingClientRect();
      if(rect.width<24 || rect.height<24) errors.push({id:element.id,text:element.textContent.slice(0,60),problem:'target below 24px',width:rect.width,height:rect.height});
    }
    if([...document.querySelectorAll('[tabindex]')].some(element=>Number(element.getAttribute('tabindex'))>0)) errors.push({problem:'positive tabindex changes focus order'});
    return errors;
  },view);
  check(view + ' has logical focus order and 24px control targets',problems.length===0,JSON.stringify(problems));
  const selector = view==='resident' ? '#report-form button[type="submit"]' : view==='control' ? '#refresh' : '#admin-refresh';
  await page.keyboard.press('Tab');
  await page.locator(selector).focus();
  await page.locator(selector).scrollIntoViewIfNeeded();
  const visible = await page.locator(selector).evaluate(element => {
    const rect=element.getBoundingClientRect(); const style=getComputedStyle(element);
    const x=Math.max(1,Math.min(innerWidth-1,rect.left+rect.width/2)); const y=Math.max(1,Math.min(innerHeight-1,rect.top+rect.height/2));
    const covering=document.elementFromPoint(x,y);
    return document.activeElement===element && rect.bottom>0 && rect.top<innerHeight && (covering===element || element.contains(covering)) && style.outlineStyle!=='none' && parseFloat(style.outlineWidth)>=2;
  });
  check(view + ' keyboard focus is visible and unobscured',visible);
}
async function top() { await page.evaluate(() => scrollTo(0, 0)); await stableFrame(); }
const noOverflow = () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1);
async function focusedTimeline() {
  // Layout-only crop of actual evidence with the native draft-history disclosure expanded.
  // Every run remains independently reviewable; paired workflow audit records remain in their disclosure.
  const draftHistory = page.locator('#agent-pipeline > .agent-run-group');
  const historyWasOpen = await draftHistory.evaluate(element => element.open);
  await draftHistory.evaluate(element => { element.open = true; });
  const stylesheet = target.origin + '/__civicops_browser_timeline.css';
  await page.route(stylesheet, route => route.fulfill({ contentType: 'text/css', body: `
    #staff-access, .language-toolbar, #degraded-service, #control > :not(.operations-grid), .operations-grid > :not(#case-detail), footer { display:none !important; }
    .operations-grid { display:block; }
    #case-detail > :not(#case-evidence) { display:none !important; }
    #case-detail { padding:20px; }
    #case-evidence { display:grid; grid-template-columns:minmax(0,1fr) minmax(0,1.08fr); gap:10px 22px; margin:0; }
    #case-evidence > .evidence-header { grid-column:1 / -1; padding:10px 0; }
    #agent-pipeline, #decision-timeline { margin:0; min-width:0; }
    #decision-timeline .timeline-item { padding-bottom:4px; font-size:11px; line-height:1.4; }
    #decision-timeline .timeline-item h3 { margin:0 0 2px; padding:0; border:0; line-height:1.35; }
    #decision-timeline .timeline-item .hint { display:block; line-height:1.3; }
    #agent-pipeline > h3, #decision-timeline > h3 { margin-top:0; padding-top:0; border:0; }
    #decision-timeline .timeline-item p { font-size:11px; }
    #decision-timeline .timeline-item .hint { font-size:10.5px; }
    #decision-timeline .timeline-item p { line-height:1.4; }
    #agent-pipeline .agent-run { margin-bottom:6px; }
    main { padding-top:16px; }
  ` }));
  const style = await page.addStyleTag({ url: stylesheet });
  try {
    await top();
    const bounds = await page.evaluate(() =>
      ['#agent-pipeline > .agent-run-group', '#decision-timeline .timeline-item:last-child'].map(selector => {
        const rect = document.querySelector(selector).getBoundingClientRect(); return { selector, top: rect.top, bottom: rect.bottom, limit: innerHeight - 12 };
      }));
    if (bounds.some(rect => rect.bottom > rect.limit)) await page.screenshot({ path: path.join(outputDirectory, 'timeline-overflow.png') });
    check('Focused timeline fits the full pipeline and final decision', bounds.every(rect => rect.top >= 0 && rect.bottom <= rect.limit), JSON.stringify(bounds));
    await shot('case-timeline');
  } finally {
    await style.evaluate(element => element.remove()); await page.unroute(stylesheet);
    await draftHistory.evaluate((element,wasOpen) => { element.open = wasOpen; },historyWasOpen);
    await top();
  }
}
const check = (name, condition, detail = '') => { assert(condition, detail ? `${name}: ${detail}` : name); checks.push(name); };
const waitText = (selector, text) => page.waitForFunction(({ selector, text }) =>
  document.querySelector(selector)?.textContent.includes(text), { selector, text });
try {
  // Keep deliberate stale-version tests stable; the app's polling fallback remains active.
  await page.route('**/api/stream', route => route.abort('failed'));
  const health = await context.request.get(baseUrl + '/healthz');
  const healthBody = await health.json();
  check('Explicit demo and outbox safety boundary', healthBody.demoMode === true && healthBody.emailMode === 'outbox');
  const navigation = await page.goto(baseUrl, { waitUntil: 'domcontentloaded' }); // SSE intentionally keeps a connection open.
  const securityHeaders = navigation.headers();
  check('HTML sends strict CSP without inline or evaluation exceptions', securityHeaders['content-security-policy']?.includes("script-src 'nonce-")
    && securityHeaders['content-security-policy'].includes("style-src 'self';")
    && securityHeaders['content-security-policy'].includes("frame-ancestors 'none'")
    && !/unsafe-inline|unsafe-eval|elevenlabs|livekit/.test(securityHeaders['content-security-policy']));
  check('Static and API replies include browser isolation and privacy headers', [securityHeaders, health.headers()].every(headers =>
    headers['x-content-type-options'] === 'nosniff' && headers['referrer-policy'] === 'no-referrer'
    && headers['cross-origin-opener-policy'] === 'same-origin' && headers['cross-origin-resource-policy'] === 'same-origin'
    && headers['permissions-policy']?.includes('microphone=()')));
  await waitText('#network', 'EXPLICIT DEMO');
  await context.request.post(baseUrl+'/api/demo/seed');
  check('Simulated agent label visible', await page.locator('#agent-mode').textContent() === 'SIMULATED');
  check('Degraded provider banner is accessible and keeps intake available', await page.locator('#degraded-service').isVisible()
    && await page.locator('#degraded-service').getAttribute('role') === 'status'
    && (await page.locator('#degraded-service').textContent()).includes('Reports are accepted')
    && (await page.locator('#degraded-service').textContent()).includes('SIMULATED'));
  const readiness = await context.request.get(baseUrl+'/health/ready');
  check('Simulated providers are degraded without failing readiness', readiness.status() === 200 && (await readiness.json()).status === 'degraded');

  const submissions = [];
  let loseFirstResponse = true;
  await page.route('**/api/incidents', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    submissions.push({ key: route.request().headers()['idempotency-key'], body: route.request().postData() });
    if (loseFirstResponse) {
      loseFirstResponse = false;
      await new Promise(resolve => setTimeout(resolve, 200));
      await route.fetch(); // Commit the case, then simulate a lost network response.
      return route.abort('failed');
    }
    return route.continue();
  });
  await page.locator('#description').fill('Synthetic stormwater drain blocked beside the demo library.');
  await page.locator('#area').fill('Synthetic North');
  await page.locator('#email').fill('resident@example.invalid');
  await page.locator('[name="consentToContact"]').check();
  check('Resident intake explains its purpose and requires explicit privacy consent', await page.locator('#privacy-consent').isVisible()
    && !await page.locator('#privacy-consent').isChecked() && (await page.locator('#privacy-purpose').textContent()).includes('Contact details are optional')
    && (await page.locator('#privacy-purpose').textContent()).includes('service issue'));
  await page.locator('#language').selectOption('af');
  await waitText('#privacy-purpose','Ek stem in');
  await page.locator('#privacy-consent').check();
  await page.locator('#report-form').evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
  await waitText('#report-result', 'Failed to fetch');
  check('Double submission guarded while pending', submissions.length === 1);
  await page.locator('#report-form button[type="submit"]').click();
  await waitText('#report-result', 'Verwysing CIV-');
  check('Lost response retry keeps the same idempotency key', submissions.length === 2 && submissions[0].key === submissions[1].key);
  check('Retry preserves the exact report payload', submissions[0].body === submissions[1].body);
  await page.locator('#ui-language').selectOption('en');
  const reference = await page.locator('#reference').inputValue();
  const casesResponse = await context.request.get(baseUrl + '/api/incidents');
  const matching = (await casesResponse.json()).filter(incident => incident.referenceNumber === reference);
  check('Retry created exactly one durable case', matching.length === 1);
  const caseId = matching[0].id;
  await page.locator('#lookup-form button').click();
  await waitText('#lookup-result', reference);
  check('Public lookup excludes the description and contact', !(await page.locator('#lookup-result').textContent()).includes('demo library')
    && !(await page.locator('#lookup-result').textContent()).includes('resident@example.invalid'));

  await page.locator('#chat-area').fill('Synthetic East');
  await page.locator('#chat-privacy-consent').check();
  await page.locator('#chat-message').fill('Synthetic streetlight power failure beside the demo field.');
  await page.locator('#chat-form button').click();
  await waitText('#chat-log', 'CIV-');
  check('Simulated chat reports through the tool workflow', (await page.locator('#chat-log .bubble.agent').last().textContent()).includes('CIV-'));

  check('Chat displays the resident message and the full agent reply', await page.locator('#chat-log .bubble.resident').count() === 1
    && (await page.locator('#chat-log .bubble.agent').last().textContent()).includes('human review'));
  await page.locator('#chat-log').evaluate(element => { element.scrollTop = 0; });
  await shot('simulated-agent-chat', page.locator('.agent-card'));
  await top();
  check('Resident desktop view has no horizontal page overflow', await noOverflow());
  check('Resident inputs have associated accessible labels', await page.locator('#report-form').evaluate(form =>
    [...form.querySelectorAll('input,textarea,select')].every(input => input.labels?.length > 0)));
  await page.locator('#description').focus();
  check('Keyboard focus uses a visible outline', await page.locator('#description').evaluate(element => {
    const style = getComputedStyle(element); return style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) >= 2;
  }));
  await page.locator('#description').blur();
  await page.locator('#description').fill('Synthetic stormwater drain blocked beside the demo library.');
  await page.locator('#area').fill('Synthetic North');
  await page.locator('#area').blur();
  check('Reduced motion disables the view entrance animation', await page.locator('#resident').evaluate(element => getComputedStyle(element).animationName === 'none'));
  await page.locator('#lookup-form button').click();
  await waitText('#lookup-result', await page.locator('#reference').inputValue());
  await top();
  // The product screenshot documents the resident desk itself; the public landing above it is captured separately.
  await page.locator('#overview').evaluate(element => { element.hidden = true; });
  const residentHeight = await page.locator('.resident-grid').evaluate(element => Math.ceil(element.getBoundingClientRect().bottom + 28));
  await page.setViewportSize({ width: 1440, height: residentHeight });
  await top();
  await shot('resident-desk');
  await page.locator('#overview').evaluate(element => { element.hidden = false; });
  await page.setViewportSize({ width: 1440, height: 1000 });

  await page.route('**/api/channels/elevenlabs/alerts', route => route.fulfill({
    contentType:'application/json', body:JSON.stringify([{referenceNumber:null,alert:'Synthetic call failed; human follow-up required.',at:'2026-10-08T10:00:00Z'}])
  }));
  await page.locator('[data-view="control"]').click();
  await page.waitForSelector('.queue-item');
  await waitText('#alert-items','human follow-up required');
  check('Unlinked conversation alerts are visible to dispatchers', await page.locator('#channel-alerts').isVisible());
  await page.locator('.queue-item').filter({ hasText: reference }).click();
  await page.waitForSelector('[data-action="approve"]');
  await page.waitForTimeout(1200); // Allow the simulated creation outbox receipt to persist.
  const latest = await (await context.request.get(baseUrl + '/api/dispatch/incidents/' + caseId)).json();
  const approved = await context.request.post(baseUrl + '/api/incidents/' + caseId + '/approve',
    { data: { approvedBy: 'Browser synthetic dispatcher', expectedVersion: latest.version } });
  assert(approved.ok(), 'External synthetic approval used to create a version conflict');
  await page.locator('[data-action="approve"]').click();
  await waitText('#action-result', 'changed');
  await page.waitForSelector('[data-action="assign"]');
  check('Version conflict refreshes case detail and available actions', await page.locator('[data-action="assign"]').isVisible());
  await page.waitForFunction(()=>document.querySelector('#crew-choice')?.options.length > 0);
  await page.waitForSelector('#crew-eligibility .qualification');
  check('Only an eligible crew appears', await page.locator('#crew-choice option').count() === 1
    && await page.locator('#crew-choice').inputValue() === 'roads-01');

  async function decision(selector, expectedStatus) {
    for (let attempt = 0; attempt < 3; attempt++) {
      await page.locator('#refresh').click();
      await page.waitForTimeout(250);
      await page.locator(selector).click();
      await page.waitForTimeout(350);
      const detail = await (await context.request.get(baseUrl + '/api/dispatch/incidents/' + caseId)).json();
      if (detail.status === expectedStatus) return detail;
    }
    throw new Error(`Dispatcher action failed to reach ${expectedStatus}.`);
  }
  await decision('[data-action="assign"]', 'Assigned');
  await decision('[data-status="InProgress"]', 'InProgress');
  const resolved = await decision('[data-status="Resolved"]', 'Resolved');
  await page.waitForFunction(async reference => {
    const response = await fetch('/api/integrations/outbox', { cache: 'no-store' });
    if (!response.ok) return false;
    const items = (await response.json()).items.filter(item => item.caseReference === reference);
    return items.length > 0 && items.every(item => item.state === 'Delivered');
  }, reference, { timeout: 10000 });
  check('Human approval, eligible assignment and resolution work in the browser', resolved.approval
    && resolved.assignedCrewId === 'roads-01' && resolved.status === 'Resolved');
  await Promise.all([page.waitForResponse(response => response.url().endsWith('/api/metrics') && response.ok()), page.locator('#refresh').click()]);
  check('Recorded agent runs and comms drafts render', await page.locator('.agent-run').count() >= 6 && await page.locator('.draft').count() >= 1);
  await page.waitForSelector('#crew-eligibility .qualification');
  check('Sub-millisecond agent timings show less than one millisecond', await page.locator('.agent-run').evaluateAll((elements,runs) =>
    runs.every((run,index) => run.latencyMs >= 1 || elements.find(element => Number(element.dataset.runIndex) === index)?.querySelector('.agent-duration').textContent === '<1 ms'),resolved.agentRuns));
  check('Agent pipeline details are collapsed by default', await page.locator('.agent-run[open],.agent-run-group[open]').count() === 0);
  const recordedDrafts = resolved.agentRuns.filter(run => run.agentName === 'CommsAgent');
  const draftGroup = page.locator('#agent-pipeline > .agent-run-group');
  check('Repeated resident update drafts use one collapsed row with the accurate count', recordedDrafts.length > 1
    && await draftGroup.count() === 1 && await draftGroup.getAttribute('data-draft-count') === String(recordedDrafts.length)
    && (await draftGroup.locator('> summary').textContent()).includes(recordedDrafts.length + ' drafts')
    && await page.locator('#agent-pipeline > .agent-run-group + .agent-run-group').count() === 0);
  check('Grouped history retains every recorded run and original sequence', await page.locator('.agent-run').count() === resolved.agentRuns.length
    && await page.locator('.agent-run').evaluateAll((elements,runs) => runs.every((run,index) =>
      elements.some(element => Number(element.dataset.runIndex) === index && element.dataset.runId === run.id)),resolved.agentRuns));
  const groupedTotal = await draftGroup.locator('> summary .agent-duration').textContent();
  const totalLatency = recordedDrafts.reduce((sum,run) => sum+run.latencyMs,0);
  const expectedTotal = totalLatency < 1 ? '<1 ms' : totalLatency.toFixed(totalLatency < 10 ? 1 : 0) + ' ms';
  check('Grouped duration reports the combined measured time', groupedTotal === expectedTotal + 'total');
  const resolvedRow = page.locator('#decision-timeline > .timeline > .timeline-item[data-event-actions="status resolved"]');
  const standaloneStatusRows = await page.locator('#decision-timeline .timeline-item[data-event-actions="status"]').count();
  check('Resolution has one human label and preserves its paired audit records', await resolvedRow.count() === 1
    && await resolvedRow.locator('> h3').textContent() === 'Case resolved'
    && await resolvedRow.locator('.timeline-source li').count() === 2
    && standaloneStatusRows === resolved.audit.filter(event => event.action === 'status').length - 1
    && await page.locator('#decision-timeline .timeline-item[data-event-actions="resolved"]').count() === 0);
  check('Case record syncs have human timeline labels and retain simulated delivery details', await page.locator('#decision-timeline .timeline-item[data-event-actions="case-record-synced"]').count() > 0
    && await page.locator('#decision-timeline .timeline-item[data-event-actions="case-record-synced"]').evaluateAll(elements =>
      elements.every(element => element.querySelector('h3').textContent === 'Case record synced' && element.textContent.includes('simulated:'))));
  await draftGroup.locator('> summary').focus();
  await page.keyboard.press('Enter');
  check('Grouped draft history expands by keyboard and exposes all drafts', await draftGroup.getAttribute('open') !== null
    && await draftGroup.locator('.agent-run').count() === recordedDrafts.length
    && await draftGroup.locator('.agent-run > summary').first().isVisible());
  await draftGroup.locator('.agent-run > summary').first().click();
  const firstDraft = recordedDrafts[0];
  check('Expanded draft retains its reasoning provider tools ID and recorded time', await draftGroup.locator('.agent-run').first().evaluate((element,run) =>
    element.open && element.textContent.includes(run.rationale) && element.textContent.includes(run.model)
    && (run.toolPermissions || []).every(tool => element.textContent.includes(tool))
    && element.dataset.runId === run.id && element.querySelector('time').getAttribute('datetime') === run.at,firstDraft));
  await resolvedRow.locator('.timeline-source > summary').focus();
  await page.keyboard.press('Enter');
  check('Lifecycle audit disclosure expands by keyboard and retains both original sources', await resolvedRow.locator('.timeline-source').getAttribute('open') !== null
    && await resolvedRow.locator('.timeline-source li').evaluateAll((elements,events) => elements.every((element,index) =>
      element.querySelector('code').textContent === events[index].action && element.textContent.includes(events[index].detail)
      && element.querySelector('time').getAttribute('datetime') === events[index].at),resolved.audit.slice(
        resolved.audit.findIndex(event => event.action === 'resolved')-1,resolved.audit.findIndex(event => event.action === 'resolved')+1)));
  await axeScan('case-history-expanded');
  await draftGroup.locator('.agent-run > summary').first().click();
  await draftGroup.locator('> summary').focus();
  await page.keyboard.press('Space');
  await resolvedRow.locator('.timeline-source > summary').focus();
  await page.keyboard.press('Space');
  check('Keyboard collapses grouped history and paired audit details', await draftGroup.getAttribute('open') === null
    && await resolvedRow.locator('.timeline-source').getAttribute('open') === null);
  check('Fallback pipeline does not repeat artificial confidence', !(await page.locator('#agent-pipeline').textContent()).includes('90% confidence')
    && (await page.locator('#agent-pipeline').textContent()).toLowerCase().includes('fallback'));
  check('Outcome strip shows seven audit-derived measures and a synthetic label', await page.locator('#outcome-metrics .kpi').count() === 7
    && /synthetic/i.test(await page.locator('#outcome-metrics').locator('..').textContent()));
  check('Control room desktop has no horizontal page overflow', await noOverflow());
  // Remove the test-only unlinked alert before taking the product shot, then refresh real state.
  await page.unroute('**/api/channels/elevenlabs/alerts');
  await Promise.all([page.waitForResponse(response => response.url().endsWith('/api/metrics') && response.ok()), page.locator('#refresh').click()]);
  await page.waitForSelector('#channel-alerts', { state: 'hidden' });
  await page.waitForSelector('#crew-eligibility .qualification');
  await page.setViewportSize({ width: 1440, height: 1500 });
  await top();
  await page.locator('#queue').evaluate(element => { element.scrollTop = 0; });
  await shot('control-room');
  check('Control composition includes header, KPI strip, queue and selected qualifications', await page.evaluate(() => {
    const selectors = ['.topbar', '#control .section-head', '#outcome-metrics', '.queue-item', '#crew-eligibility .qualification'];
    return selectors.every(selector => { const rect = document.querySelector(selector).getBoundingClientRect(); return rect.top >= 0 && rect.top < innerHeight && rect.right <= innerWidth; });
  }), await page.evaluate(() => ['.topbar', '#control .section-head', '#outcome-metrics', '.queue-item', '#crew-eligibility .qualification', '#sos-control'].map(selector => {
    const rect = document.querySelector(selector)?.getBoundingClientRect();
    return rect ? `${selector} top=${Math.round(rect.top)} bottom=${Math.round(rect.bottom)} right=${Math.round(rect.right)}` : `${selector} missing`;
  }).concat([`viewport=${innerWidth}x${innerHeight}`]).join(' | ')));
  await page.setViewportSize({ width: 1440, height: 1000 });
  await focusedTimeline();
  // The seeded electrical crew has a genuinely expired synthetic authorisation; do not fabricate a badge.
  const chatCase = (await (await context.request.get(baseUrl + '/api/incidents')).json())
    .find(incident => incident.description === 'Synthetic streetlight power failure beside the demo field.');
  assert(chatCase, 'Synthetic electrical chat case exists');
  await page.locator('.queue-item').filter({ hasText: chatCase.referenceNumber }).click();
  await page.waitForSelector('#crew-eligibility .status-badge.expired', { state: 'attached' });
  await page.locator('#crew-eligibility details').first().evaluate(element => { element.open = true; });
  check('Expired certification and warned eligibility have clear status badges', await page.locator('#crew-eligibility .status-badge.expired').first().isVisible()
    && await page.locator('#crew-eligibility .status-badge.warned').first().isVisible());
  check('Crew eligibility reasons form a scannable list', await page.locator('#crew-eligibility .eligibility-reasons li').count() > 0);
  check('Details summaries inherit the design font and expose a custom chevron', await page.locator('#crew-eligibility summary').first().evaluate(element =>
    getComputedStyle(element).fontFamily === getComputedStyle(document.body).fontFamily && getComputedStyle(element, '::before').content !== 'none'));
  await shot('crew-certifications', page.locator('#crew-eligibility'));
  await page.locator('.queue-item').filter({ hasText: reference }).click();
  await page.waitForSelector('#crew-eligibility .qualification');
  await page.screenshot({ path: path.join(outputDirectory, 'desktop-control-room.png'), fullPage: true });

  await page.setViewportSize({ width: 390, height: 1100 });
  check('Mobile control room has no horizontal page overflow', await noOverflow());
  await page.locator('[data-view="resident"]').click();
  await top();
  await shot('mobile-resident');
  check('Mobile resident view has no horizontal page overflow', await noOverflow());
  await context.setOffline(true);
  await waitText('#network', 'OFFLINE');
  await page.locator('#description').fill('Synthetic offline report remains a local draft.');
  await page.locator('#area').fill('Synthetic South');
  await page.locator('#privacy-consent').check();
  await page.locator('#report-form button[type="submit"]').click();
  await waitText('#report-result', 'Offline.');
  check('Offline submission preserves the draft and creates no request', await page.locator('#description').inputValue() === 'Synthetic offline report remains a local draft.' && submissions.length === 2);
  await context.setOffline(false);
  await page.locator('[data-view="audit"]').click();
  await page.locator('#verify-audit').click();
  await waitText('#audit-result', 'VERIFIED');
  check('Browser audit view verifies the durable chain', await page.locator('#audit-rows tr').count() > 0);
  check('Mobile audit view has no horizontal page overflow', await noOverflow());
  await page.setViewportSize({width:1440,height:1000});
  await page.evaluate(()=>scrollTo(0,0));
  check('Desktop audit view has no horizontal page overflow', await noOverflow());
  await shot('audit-verify');
  const downloadEvent = page.waitForEvent('download');
  await page.locator('#export-evidence').click();
  const download = await downloadEvent;
  await download.saveAs(path.join(outputDirectory, 'evidence.json'));
  const evidence = await readFile(path.join(outputDirectory, 'evidence.json'), 'utf8');
  check('Evidence download excludes resident contact and report text', !evidence.includes('resident@example.invalid') && !evidence.includes('demo library') && JSON.parse(evidence).verification.valid === true);
  // Exercise the private browser orchestration with an offline SDK double; never contact a provider.
  for (const mode of ['signed-url','webrtc']) {
    const privatePage=await context.newPage();
    privatePage.on('pageerror',error=>runtimeErrors.push(error.message));
    await privatePage.addInitScript(()=>{ window.__sessions=[]; window.__sent=[]; window.ElevenLabsClient={Conversation:{startSession:async options=>{window.__sessions.push(options); options.onMessage({source:'ai',message:'Synthetic private agent connected.'}); return {endSession:async()=>options.onDisconnect(),sendUserMessage:message=>window.__sent.push(message)};}}}; });
    await privatePage.route('**/api/channels/status',route=>route.fulfill({contentType:'application/json',body:JSON.stringify({elevenlabs:{mode:'live',sessionMode:mode,callbacks:'disabled'}})}));
    let sessionRequests=0;
    await privatePage.route('**/api/channels/elevenlabs/session',route=>{sessionRequests++; return route.fulfill({contentType:'application/json',body:JSON.stringify(mode==='webrtc' ? {mode:'live',sessionMode:mode,token:'synthetic-token',conversation_id:'synthetic-conversation'} : {mode:'live',sessionMode:mode,signed_url:'wss://synthetic.invalid/session'})});});
    await privatePage.goto(baseUrl,{waitUntil:'domcontentloaded'});
    await privatePage.waitForSelector('#private-agent:not([hidden])');
    check(`${mode} credentials requested only when the resident starts`,sessionRequests===0);
    await privatePage.locator('#ui-language').selectOption('af');
    await privatePage.locator(mode==='webrtc' ? '#private-voice' : '#private-text').click();
    await privatePage.waitForFunction(()=>window.__sessions.length===1);
    const session=await privatePage.evaluate(()=>({signedUrl:window.__sessions[0].signedUrl,conversationToken:window.__sessions[0].conversationToken,connectionType:window.__sessions[0].connectionType}));
    check(`${mode} uses the official client credential contract`,mode==='webrtc' ? session.conversationToken==='synthetic-token' && session.connectionType==='webrtc' : session.signedUrl==='wss://synthetic.invalid/session' && session.connectionType==='websocket');
    await privatePage.locator('#private-message').fill('Synthetic private message'); await privatePage.locator('#private-send').click();
    check(`${mode} text messages reach the client`,await privatePage.evaluate(()=>window.__sent[0]==='Synthetic private message'));
    await privatePage.locator('#private-stop').click();
    check(`${mode} session can end cleanly`,await privatePage.locator('#private-stop').isDisabled());
    check(`${mode} starts with the configured Afrikaans language preset`,await privatePage.evaluate(()=>window.__sessions[0].overrides.agent.language==='af'));
    await privatePage.locator('#ui-language').selectOption('zu');
    await privatePage.waitForSelector('#private-agent-note:not([hidden])');
    await privatePage.locator('#private-voice').evaluate(element=>element.click());
    check(`${mode} honestly disables unverified isiZulu live voice without requesting credentials`,await privatePage.locator('#private-agent').isHidden() && sessionRequests===1
      && await privatePage.evaluate(()=>document.querySelector('#private-agent-note').textContent===i18n.text('ui.zuluVoiceUnavailable')));
    await privatePage.locator('#ui-language').selectOption('en');
    await privatePage.close();
  }
  // Enterprise administration uses only local synthetic sessions and integrations.
  // Chrome accepts Secure loopback cookies; Playwright's APIRequestContext deliberately does not send them over HTTP.
  const browserRequest = (route,options={}) => page.evaluate(async ({route,options}) => {
    const response=await fetch(route,{...options,credentials:'same-origin',cache:'no-store'});
    const text=await response.text(); let body=null; try { body=JSON.parse(text); } catch { /* Empty denials are valid. */ }
    return {status:response.status,body};
  },{route:baseUrl+route,options});
  await page.locator('#staff-access').evaluate(element => { element.open = true; });
  check('Demo staff access is explicitly labelled and offers all eight roles',
    /EXPLICIT DEMO/.test(await page.locator('#demo-login-form').textContent()) && await page.locator('#demo-role option').count() === 8);
  async function demoRole(role, tenant = 'default') {
    await page.locator('#staff-access').evaluate(element => { element.open = true; });
    await page.locator('#demo-role').selectOption(role); await page.locator('#demo-tenant').selectOption(tenant);
    await page.locator('#demo-login-form button').click();
    await page.waitForFunction(({role,tenant}) => state.auth?.authenticated && state.auth.roles?.includes(role) && state.auth.tenantId === tenant,{role,tenant});
    await waitText('#staff-result','EXPLICIT DEMO');
    await page.locator('#demo-login-form button').waitFor({state:'visible'});
    await page.waitForFunction(()=>!document.querySelector('#demo-login-form button').disabled);
  }
  await demoRole('resident');
  await page.locator('[data-view="resident"]').click();
  await page.locator('#chat-area').fill('Synthetic resident session');
  await page.locator('#chat-message').fill('Synthetic resident session reports a blocked stormwater drain beside the fictitious library.');
  let residentReplies=await page.locator('#chat-log .bubble.agent').count();
  await page.locator('#chat-form button').click();
  await page.waitForFunction(count=>document.querySelectorAll('#chat-log .bubble.agent').length>count,residentReplies);
  const residentReply=await page.locator('#chat-log .bubble.agent').last().textContent();
  const residentChatReference=residentReply.match(/CIV-\d{8}-[A-F0-9]{8}/i)?.[0];
  const residentReport=residentChatReference ? await browserRequest('/api/incidents/'+residentChatReference) : null;
  check('Authenticated demo residents can report through simulated chat',!!residentChatReference && residentReport.status===200
    && residentReport.body.referenceNumber===residentChatReference && /human review/.test(residentReply));
  await page.locator('#chat-message').fill('Check '+residentChatReference);
  residentReplies=await page.locator('#chat-log .bubble.agent').count();
  await page.locator('#chat-form button').click();
  await page.waitForFunction(count=>document.querySelectorAll('#chat-log .bubble.agent').length>count,residentReplies);
  const residentLookup=await page.locator('#chat-log .bubble.agent').last().textContent();
  check('Authenticated resident chat lookup uses the minimal public case view',residentLookup.includes(residentChatReference)
    && !residentLookup.includes('fictitious library') && !/does not permit|retry this same message/.test(residentLookup));
  await demoRole('auditor');
  const deniedAdmin = await browserRequest('/api/admin/overview');
  check('Auditor is denied administration by the server', deniedAdmin.status === 403);
  check('Administration navigation is hidden for a non-admin role', await page.locator('[data-view="admin"]').isHidden());
  await page.evaluate(()=>{location.hash='admin';});
  await page.waitForSelector('#admin-locked:not([hidden])');
  check('Direct administration navigation exposes no tenant data to an auditor',await page.locator('#admin-content').isHidden());
  await demoRole('admin');
  await page.locator('[data-view="admin"]').click();
  await page.waitForSelector('#admin-content:not([hidden])');
  check('Admin overview renders all seven tenant configuration and privacy panels plus channel templates', await page.locator('#admin-content .admin-panel').count() === 8
    && await page.locator('#admin-channel-templates.admin-panel').count() === 1
    && await page.locator('#admin-content').evaluate(element=> {
      const existing=['admin-tenants','admin-users','admin-service-accounts','admin-catalogue-form','admin-crews','admin-workers','admin-privacy-requests'];
      const panels=existing.map(id=>element.querySelector('#'+id)?.closest('.admin-panel'));
      return panels.every(Boolean) && new Set(panels).size===7;
    })
    && (await page.locator('#admin-tenant').textContent()).includes('SYNTHETIC'));
  check('Admin form fields have associated accessible labels',await page.locator('#admin-content').evaluate(element =>
    [...element.querySelectorAll('input,textarea,select')].every(input=>input.labels?.length>0)));
  await page.locator('#admin-privacy-create').locator('..').evaluate(element=>{element.open=true;});
  await page.locator('#admin-privacy-reference').fill(reference);
  await page.locator('#admin-privacy-note').fill('Synthetic resident requests a local access export.');
  await page.locator('#admin-privacy-create button').click();
  await waitText('#admin-privacy-result','Request recorded');
  check('Administrator records a tenant-bound privacy request', (await page.locator('#admin-privacy-requests').textContent()).includes(reference));
  await page.locator('#admin-privacy-verify').locator('..').evaluate(element=>{element.open=true;});
  await page.locator('#admin-privacy-verification').fill('Synthetic identity verified through an established municipal channel; no documents retained.');
  await page.locator('#admin-privacy-verify button').click();
  await waitText('#admin-privacy-result','Identity-verification note recorded');
  check('Privacy fulfilment records identity verification', (await page.locator('#admin-privacy-requests').textContent()).includes('identity verified'));
  const privacyDownloadEvent = page.waitForEvent('download');
  await page.locator('#admin-privacy-fulfil button').click();
  const privacyDownload = await privacyDownloadEvent;
  await privacyDownload.saveAs(path.join(outputDirectory,'privacy-access.json'));
  await waitText('#admin-privacy-result','access export downloaded');
  const privacyExport = JSON.parse(await readFile(path.join(outputDirectory,'privacy-access.json'),'utf8'));
  check('Verified privacy access downloads local JSON and leaves a fulfilled audit record', privacyExport.municipality==='default'
    && JSON.stringify(privacyExport).includes(reference) && (await page.locator('#admin-privacy-requests').textContent()).includes('fulfilled'));
  await page.locator('#admin-refresh').focus(); await page.keyboard.press('Enter');
  await page.waitForFunction(()=>!document.querySelector('#admin-refresh').disabled);
  check('Administration refresh is keyboard accessible with visible focus', await page.locator('#admin-refresh').evaluate(element=> {
    const style=getComputedStyle(element); return document.activeElement===element && style.outlineStyle!=='none' && parseFloat(style.outlineWidth)>=2;
  }));
  const csrfRejected = await browserRequest('/api/admin/service-accounts',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:'Synthetic missing CSRF',scopes:['tools:invoke'],expiresAt:new Date(Date.now()+86400000).toISOString()})});
  check('Cookie-authenticated administration rejects a missing CSRF token',csrfRejected.status===400 || csrfRejected.status===403);

  await page.locator('#admin-user-form').locator('..').evaluate(element=>{element.open=true;});
  await page.locator('#admin-user-subject').fill('synthetic-browser-supervisor');
  await page.locator('#admin-user-name').fill('Synthetic browser supervisor');
  await page.locator('#admin-user-roles input[value="dispatcher"]').uncheck();
  await page.locator('#admin-user-roles input[value="supervisor"]').check();
  await page.locator('#admin-user-form button').click();
  await waitText('#admin-user-result','Staff user created');
  const staffToken = await page.locator('#admin-new-token').inputValue();
  const users = (await browserRequest('/api/admin/users')).body;
  const staffUser=users.find(user=>user.subject==='synthetic-browser-supervisor');
  check('Admin creates tenant-bound staff roles with a token shown once',!!staffUser && staffUser.tenantId==='default'
    && staffUser.roles.includes('supervisor') && staffToken.length>20 && !JSON.stringify(users).includes(staffToken));
  await page.locator('#admin-token-dismiss').click();
  check('Dismissing a shown-once token clears its value',await page.locator('#admin-new-token').inputValue()==='' && await page.locator('#admin-token-reveal').isHidden());

  await page.locator('#admin-service-form').locator('..').evaluate(element=>{element.open=true;});
  await page.locator('#admin-service-name').fill('Synthetic browser voice tools');
  await page.locator('#admin-service-form button').click();
  await waitText('#admin-service-result','Service account created');
  const firstToken=await page.locator('#admin-new-token').inputValue();
  const accounts=(await browserRequest('/api/admin/service-accounts')).body;
  const account=accounts.find(item=>item.name==='Synthetic browser voice tools');
  check('Admin creates a scoped service account without returning token hashes in lists',!!account && account.tenantId==='default'
    && account.scopes.length===1 && account.scopes[0]==='tools:invoke' && !JSON.stringify(accounts).includes(firstToken) && !/tokenHash/i.test(JSON.stringify(accounts)));
  await page.locator('#admin-token-dismiss').click();
  await page.locator(`[data-account-rotate="${account.id}"]`).click();
  await waitText('#admin-service-result','Token rotated');
  const rotatedToken=await page.locator('#admin-new-token').inputValue();
  check('Admin rotates a service account and explains the overlap window',rotatedToken!==firstToken && rotatedToken.length>20
    && /five-minute/.test(await page.locator('#admin-token-detail').textContent()));
  await page.locator('#admin-token-dismiss').click();
  await page.locator(`[data-account-revoke="${account.id}"]`).click();
  await waitText('#admin-service-result','Service account revoked');
  const revoked=await browserRequest('/api/channels/elevenlabs/tools/lookup_case_status',{method:'POST',headers:{Authorization:'Bearer '+rotatedToken,'Content-Type':'application/json'},body:JSON.stringify({reference_number:reference})});
  check('Admin revocation disables the rotated service token',revoked.status===401 || revoked.status===403);

  await page.locator('#admin-catalogue-form').locator('..').evaluate(element=>{element.open=true;});
  const catalogue=JSON.parse(await page.locator('#admin-catalogue-json').inputValue());
  const invalidCatalogue=structuredClone(catalogue); delete invalidCatalogue.workOrderTypes[0].title;
  await page.locator('#admin-catalogue-json').fill(JSON.stringify(invalidCatalogue,null,2));
  await page.locator('#admin-catalogue-form button').click();
  await page.waitForSelector('#admin-catalogue-result.error');
  check('Catalogue schema errors retain the admin editor draft',JSON.parse(await page.locator('#admin-catalogue-json').inputValue()).workOrderTypes[0].title===undefined);
  const versionParts=catalogue.version.split('.').map(Number); versionParts[2]++;
  catalogue.version=versionParts.join('.'); catalogue.workOrderTypes[0].title+=' (synthetic admin review)';
  await page.locator('#admin-catalogue-json').fill(JSON.stringify(catalogue,null,2));
  await page.locator('#admin-catalogue-form button').click();
  await waitText('#admin-catalogue-result','New catalogue version validated');
  check('Valid catalogue edits advance the persisted version', (await page.locator('#admin-catalogue-version').textContent()).includes(catalogue.version));

  await page.locator('#admin-crew-form').locator('..').evaluate(element=>{element.open=true;});
  const crew=JSON.parse(await page.locator('#admin-crew-json').inputValue()); crew.capability+=' · synthetic admin review';
  await page.locator('#admin-crew-json').fill(JSON.stringify(crew,null,2)); await page.locator('#admin-crew-form button').click();
  await waitText('#admin-crew-result','Crew validated');
  check('Admin can save a validated municipal crew',(await page.locator('#admin-crews').textContent()).includes('synthetic admin review'));
  await page.locator('#admin-worker-form').locator('..').evaluate(element=>{element.open=true;});
  const worker=JSON.parse(await page.locator('#admin-worker-json').inputValue()); worker.displayName+=' · reviewed';
  await page.locator('#admin-worker-json').fill(JSON.stringify(worker,null,2)); await page.locator('#admin-worker-form button').click();
  await waitText('#admin-worker-result','Worker qualifications validated');
  check('Admin saves worker qualifications while preserving certifications',JSON.parse(await page.locator('#admin-worker-json').inputValue()).certifications.length===worker.certifications.length
    && (await page.locator('#admin-workers').textContent()).includes('reviewed'));
  await page.locator('#admin-tenant-form').locator('..').evaluate(element=>{element.open=true;});
  const tenant=JSON.parse(await page.locator('#admin-tenant-json').inputValue()); tenant.serviceArea+=' Synthetic admin review.';
  await page.locator('#admin-tenant-json').fill(JSON.stringify(tenant,null,2)); await page.locator('#admin-tenant-form button').click();
  await waitText('#admin-tenant-result','Municipality settings validated');
  check('Admin saves validated municipality settings within its tenant boundary',(await page.locator('#admin-tenants').textContent()).includes('Synthetic admin review.'));

  await page.locator('#staff-access').evaluate(element=>{element.open=true;});
  await page.locator('#bootstrap-token').fill(staffToken); await page.locator('#bootstrap-login-form button').click();
  await waitText('#staff-result','server-verified');
  check('Created bootstrap token signs in with the assigned supervisor role',await page.evaluate(()=>state.auth.authenticated && state.auth.roles.includes('supervisor') && state.auth.tenantId==='default'));
  await page.evaluate(async()=>staffTransition('/auth/demo-login',{role:'crew',tenantId:'default',crewId:'roads-01'},'EXPLICIT DEMO · Synthetic assigned crew.'));
  await page.locator('[data-view="control"]').click(); await page.waitForSelector('.queue-item');
  check('Crew control room lists only its assigned work',await page.evaluate(()=>state.cases.length>0 && state.cases.every(incident=>incident.assignedCrewId==='roads-01')));
  await page.locator('.queue-item').filter({hasText:reference}).click();
  check('Crew view hides dispatch decisions and operations-only integrations',await page.locator('#case-detail [data-action],#case-detail [data-draft]').count()===0
    && await page.locator('#control .metrics-card').isHidden() && await page.locator('#control .outbox-card').isHidden() && await page.locator('#control-error').isHidden());
  await demoRole('dispatcher');
  await page.waitForFunction(()=>state.cases.some(incident=>['high','critical'].includes(incident.workOrderType?.hazardClass)));
  const hazardReference=await page.evaluate(()=>state.cases.find(incident=>['high','critical'].includes(incident.workOrderType?.hazardClass)).referenceNumber);
  await page.locator('.queue-item').filter({hasText:hazardReference}).click();
  check('High-hazard assignments explain the required supervisor approval',await page.locator('#case-detail [data-action="approve"],#case-detail [data-action="assign"]').count()===0
    && (await page.locator('#case-detail .detail-actions').textContent()).includes('supervisor must approve'));
  check('Dispatcher view hides supervisor-only routing overrides',await page.locator('#case-detail .routing-override').count()===0);
  await demoRole('admin','synthetic-coast');
  await page.locator('[data-view="admin"]').click();
  await page.waitForSelector('#admin-content:not([hidden])');
  check('Switching municipality clears another tenant’s service accounts and local users',!(await page.locator('#admin-service-accounts').textContent()).includes('Synthetic browser voice tools')
    && !(await page.locator('#admin-users').textContent()).includes('Synthetic browser supervisor'));
  await demoRole('admin'); await page.waitForSelector('#admin-content:not([hidden])');
  await page.locator('#admin-service-form').locator('..').evaluate(element=>{element.open=true;});
  await page.locator('#admin-service-name').fill('Synthetic training connector');
  await page.locator('#admin-service-scopes input[value="tools:invoke"]').uncheck();
  await page.locator('#admin-service-scopes input[value="training:read"]').check();
  await page.locator('#admin-service-scopes input[value="training:write"]').check();
  await page.locator('#admin-service-form button').click(); await waitText('#admin-service-result','Service account created');
  check('Active integration accounts display each allowed scope',await page.locator('#admin-service-accounts .admin-record').filter({hasText:'Synthetic training connector'}).locator('.admin-badges .pill').count()===2);
  await page.locator('#staff-access').evaluate(element=>{element.open=false;});
  await page.locator('#admin-content details').evaluateAll(elements=>elements.forEach(element=>{element.open=false;}));
  await page.locator('#admin-token-dismiss').evaluate(element=>element.click());
  await page.setViewportSize({width:1440,height:1850}); await top();
  check('Desktop administration has no horizontal page overflow',await noOverflow());
  await shot('admin-console');
  await page.setViewportSize({width:390,height:1100});
  check('Mobile administration has no horizontal page overflow',await noOverflow());
  // Automated accessibility covers populated desks, expanded admin forms, both OS themes and reflow.
  await page.setViewportSize({width:1440,height:1000});
  await page.keyboard.press('Tab'); // Establish keyboard modality before programmatic focus probes.
  for(const theme of ['light','dark']) {
    await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});
    for(const view of ['resident','control','admin']) {
      await page.locator('[data-view="' + view + '"]').click();
      if(view==='control') await page.waitForSelector('.queue-item');
      if(view==='admin') {
        await page.waitForSelector('#admin-content:not([hidden])');
        await page.locator('#admin-content details').evaluateAll(elements=>elements.forEach(element=>{element.open=true;}));
      }
      await top(); await axeScan(theme + '-' + view + '-desktop');
      await focusAndTargets(view);
      // A 720 CSS-pixel layout is the available width at 200% zoom on the 1440px desktop.
      await page.setViewportSize({width:720,height:800}); await top();
      check(theme + ' ' + view + ' reflows at the 200% desktop zoom equivalent',await noOverflow());
      await page.setViewportSize({width:320,height:900}); await top();
      check(theme + ' ' + view + ' reflows at 320 CSS pixels',await noOverflow());
      await axeScan(theme + '-' + view + '-320');
      await page.screenshot({path:path.join(outputDirectory,'accessibility-' + theme + '-' + view + '-320.png'),animations:'disabled'});
      await page.setViewportSize({width:1440,height:1000});
    }
  }
  await page.emulateMedia({colorScheme:'light',reducedMotion:'reduce'});
  await page.locator('[data-view="resident"]').click();
  await page.locator('#description').focus(); await page.keyboard.press('Tab');
  check('Resident focus advances from narrative to area',await page.locator('#area').evaluate(element=>document.activeElement===element));
  await page.keyboard.press('Tab');
  check('Resident focus advances from area to language',await page.locator('#language').evaluate(element=>document.activeElement===element));
  await page.keyboard.press('Tab');
  check('Resident focus advances from language to optional contact',await page.locator('#email').evaluate(element=>document.activeElement===element));
  for(const [language,title,success] of [['af','Rapporteer ’n diensprobleem','Verwysing'],['zu','Bika inkinga yenkonzo','Inombolo yecala']]) {
    await page.locator('#ui-language').selectOption(language);
    await waitText('#resident [data-i18n="ui.issueTitle"]',title);
    check(language + ' resident language switch translates labels and document language',await page.locator('html').getAttribute('lang')===language && await page.locator('#language').inputValue()===language
      && (await page.locator('#translation-review').textContent()).length>30 && await page.locator('#control').getAttribute('lang')==='en' && await page.locator('#agent-mode').textContent()==='SIMULATED');
    await page.locator('#description').fill('Synthetic localized stormwater drain report for offline browser verification.');
    await page.locator('#area').fill('Synthetic translation ward'); await page.locator('#privacy-consent').check();
    await page.locator('#report-form button[type="submit"]').click(); await waitText('#report-result',success + ' ');
    const localizedReference=await page.locator('#reference').inputValue();
    const lookup=await browserRequest('/api/incidents/' + localizedReference);
    check(language + ' resident submission produces localized server status',lookup.status===200 && lookup.body.residentLanguage===language
      && !lookup.body.publicUpdate.includes('Report received'));
    await page.locator('#chat-area').fill('Synthetic translation ward'); await page.locator('#chat-message').fill('Synthetic localized pothole beside the fictitious library.');
    await page.locator('#chat-privacy-consent').check();
    const replies=await page.locator('#chat-log .bubble.agent').count();
    await page.locator('#chat-form button').click();
    await page.waitForFunction(count=>document.querySelectorAll('#chat-log .bubble.agent').length>count,replies);
    check(language + ' simulated chat sends the chosen language to server tools',(await page.locator('#chat-log .bubble.agent').last().textContent()).includes('CIV-')
      && !(await page.locator('#chat-log .bubble.agent').last().textContent()).includes('Your report is recorded'));
    await page.setViewportSize({width:320,height:900}); await top();
    check(language + ' translated resident layout reflows at 320 CSS pixels',await noOverflow());
    await axeScan(language + '-resident-320');
    await page.screenshot({path:path.join(outputDirectory,'accessibility-' + language + '-resident-320.png'),animations:'disabled'});
    await page.setViewportSize({width:1440,height:1000});
  }
  await page.locator('#ui-language').selectOption('en');
  await page.locator('[data-view="admin"]').click();
  await page.locator('#staff-access').evaluate(element=>{element.open=true;});
  await page.locator('#staff-signout').click(); await waitText('#staff-result','session ended');
  check('Staff sign-out clears administration data and shown-once token',await page.locator('#admin-content').isHidden()
    && await page.locator('#admin-new-token').inputValue()==='' && await page.locator('#admin-users').textContent()==='');

  check('No browser runtime JavaScript errors', runtimeErrors.length === 0);
  await stableFrame();
  check('All desktop, mobile and private-session flows have no CSP violations', cspViolations.length === 0, JSON.stringify(cspViolations));
  for (const file of screenshots) {
    const bytes = await readFile(file);
    check(`${path.basename(file)} is a legible PNG below 1 MB`, (await stat(file)).size < 1_000_000
      && bytes.readUInt32BE(16) >= (path.basename(file) === 'mobile-resident.png' ? 390 : 600) && bytes.readUInt32BE(20) >= 300);
  }
  console.log(JSON.stringify({ passed: checks.length, checks, screenshots, accessibilityReports, artifacts: outputDirectory, updateMedia: process.env.CIVICOPS_UPDATE_MEDIA === '1' }, null, 2));
} finally { await context.close(); await browser.close(); }
