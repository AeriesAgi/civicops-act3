import http from 'k6/http';
import { check, sleep } from 'k6';
import { base, headers, trainingHeaders, thresholds, summary, catalogue } from './common.js';
export const options = {
  scenarios: { training: { executor: 'shared-iterations', vus: 3, iterations: 12, maxDuration: '12s', gracefulStop: '0s' } },
  thresholds: { ...thresholds, iterations: ['count==12'] },
};
export function setup() { return catalogue(); }
export default function (type) {
  const catalogueResponse = http.get(`${base}/api/v1/training/skills`, { headers: trainingHeaders, timeout: '5s' });
  check(catalogueResponse, { 'catalogue version and ETag available': response =>
    response.status === 200 && !!response.headers.Etag && !!response.json().version });
  const request = {
    workerId: 'synthetic-water-01-a', moduleId: 'synthetic-k6-water-module',
    workOrderTypeCode: type.code, skillsPractised: type.requiredSkills.map(skill => skill.code),
    score: 92, passed: true, durationSeconds: 480, attempt: 1,
    deviceClientLabel: 'synthetic-k6-device', evidenceHash: 'a'.repeat(64),
    completedAt: new Date(Date.now() - 60000).toISOString(),
  };
  const completion = http.post(`${base}/api/v1/training/completions`, JSON.stringify(request), {
    headers: { ...headers, ...trainingHeaders, 'Idempotency-Key': `synthetic-k6-training-${__VU}-${__ITER}` }, timeout: '5s',
  });
  check(completion, { 'training completion recorded': response => response.status === 201 && !!response.json().completion.id });
  const qualifications = http.get(`${base}/api/v1/training/workers/${request.workerId}/qualifications`, { headers: trainingHeaders, timeout: '5s' });
  check(qualifications, { 'qualifications readable': response => response.status === 200 && response.json().synthetic === true });
  sleep(0.2);
}
export const handleSummary = summary;
