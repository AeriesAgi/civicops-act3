import http from 'k6/http';
import { check, sleep } from 'k6';
import { base, dispatchHeaders, thresholds, summary } from './common.js';
export const options = {
  scenarios: { queue: { executor: 'constant-vus', vus: 5, duration: '8s', gracefulStop: '2s' } },
  thresholds: { ...thresholds, http_req_duration: ['p(95)<1000'], iterations: ['count>=5'] },
};
export default function () {
  const queue = http.get(`${base}/api/incidents`, { headers: dispatchHeaders, timeout: '5s' });
  check(queue, { 'dispatcher queue contains synthetic cases': response =>
    response.status === 200 && Array.isArray(response.json()) && response.json().length >= 15 });
  const metrics = http.get(`${base}/api/metrics`, { headers: dispatchHeaders, timeout: '5s' });
  check(metrics, { 'synthetic metrics readable': response => response.status === 200 });
  sleep(0.2);
}
export const handleSummary = summary;
