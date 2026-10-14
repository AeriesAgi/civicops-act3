import http from 'k6/http';
import { check, sleep } from 'k6';
import { base, headers, thresholds, summary } from './common.js';
export const options = {
  scenarios: { burst: { executor: 'shared-iterations', vus: 3, iterations: 20, maxDuration: '15s', gracefulStop: '0s' } },
  thresholds: { ...thresholds, iterations: ['count==20'] },
};
export default function () {
  const key = `synthetic-k6-intake-${__VU}-${__ITER}`;
  const payload = JSON.stringify({
    description: `Synthetic load test pipe leak ${__VU}-${__ITER}`,
    area: `Fictitious load ward ${__VU}-${__ITER}`, residentLanguage: 'en',
  });
  const result = http.post(`${base}/api/incidents`, payload, { headers: { ...headers, 'Idempotency-Key': key }, timeout: '5s' });
  check(result, { 'report accepted with public reference': response =>
    response.status === 201 && /^CIV-/.test(response.json().incident.referenceNumber || '') });
  // Exercise retry semantics without creating another case.
  if (__ITER === 0) {
    const replay = http.post(`${base}/api/incidents`, payload,
      { headers: { ...headers, 'Idempotency-Key': key }, timeout: '5s' });
    check(replay, { 'retry replays exactly once': response => response.status === 200 && response.json().replayed === true });
  }
  sleep(0.2);
}
export const handleSummary = summary;
