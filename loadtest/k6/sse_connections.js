import { browser } from 'k6/browser';
import { check } from 'k6';
import { Trend } from 'k6/metrics';
import { base, summary } from './common.js';
const firstEvent = new Trend('sse_first_event_ms', true);
const fanout = new Trend('sse_refresh_ms', true);
export const options = {
  scenarios: { sse: { executor: 'per-vu-iterations', vus: 3, iterations: 1, maxDuration: '12s',
    gracefulStop: '0s', options: { browser: { type: 'chromium' } } } },
  thresholds: { checks: ['rate==1'], iterations: ['count==3'],
    sse_first_event_ms: ['p(95)<2000'], sse_refresh_ms: ['p(95)<2500'] },
};
export default async function () {
  const page = await browser.newPage();
  try {
    // Static local page avoids application polling/SDK activity influencing SSE measurements.
    await page.goto(`${base}/offline.html`);
    const result = await page.evaluate(async ({ label }) => {
      const started = performance.now();
      const stream = new EventSource('/api/stream');
      const received = [];
      let opened = null;
      let sent = null;
      let changed = null;
      const wait = (event, timeout) => new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`Missing SSE ${event}`)), timeout);
        stream.addEventListener(event, () => { clearTimeout(timer); resolve(performance.now()); }, { once: true });
        stream.addEventListener('error', () => { clearTimeout(timer); reject(new Error('SSE disconnected early')); }, { once: true });
      });
      try {
        stream.addEventListener('refresh', () => received.push('refresh'));
        opened = await wait('ready', 3000);
        const notification = wait('refresh', 4000);
        sent = performance.now();
        const intake = await fetch('/api/incidents', { method: 'POST', headers: {
          'Content-Type': 'application/json', 'Idempotency-Key': label,
        }, body: JSON.stringify({ description: `Synthetic SSE load pipe leak ${label}`, area: 'Fictitious SSE ward' }) });
        if (intake.status !== 201) throw new Error(`SSE trigger intake HTTP ${intake.status}`);
        changed = await notification;
        await new Promise(resolve => setTimeout(resolve, 2000));
        return { readyMs: opened - started, refreshMs: changed - sent, refreshes: received.length,
          stayedOpen: stream.readyState === EventSource.OPEN };
      } finally { stream.close(); }
    }, { label: `synthetic-k6-sse-${__VU}` });
    firstEvent.add(result.readyMs); fanout.add(result.refreshMs);
    check(result, { 'SSE ready and refresh received while connection stays open': value => value.refreshes > 0 && value.stayedOpen });
  } finally { await page.close(); }
}
export const handleSummary = summary;
