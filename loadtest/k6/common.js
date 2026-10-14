import http from 'k6/http';

export const base = __ENV.CIVICOPS_LOADTEST_BASE_URL;
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(base || '')) {
  throw new Error('The isolated load harness only accepts a loopback HTTP server.');
}
export const headers = { 'Content-Type': 'application/json' };
export const dispatchHeaders = { Authorization: `Bearer ${__ENV.CIVICOPS_DISPATCH_TOKEN}` };
export const trainingHeaders = { Authorization: 'Bearer civicops-demo-training' };
export const thresholds = {
  checks: ['rate==1'], http_req_failed: ['rate==0'],
  http_req_duration: ['p(95)<2500'],
};
export function summary(data) {
  return { [__ENV.CIVICOPS_LOADTEST_SUMMARY]: JSON.stringify(data, null, 2) };
}
export function catalogue() {
  const result = http.get(`${base}/api/v1/training/work-order-types`, { headers: trainingHeaders });
  if (result.status !== 200) throw new Error('Synthetic training catalogue unavailable.');
  return result.json().workOrderTypes.find(type => type.code === 'WATER.BURST_MAIN');
}
