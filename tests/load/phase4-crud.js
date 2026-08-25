import http from 'k6/http';
import { check } from 'k6';

const baseUrl = __ENV.NEXORA_BASE_URL;
const token = __ENV.NEXORA_ACCESS_TOKEN;
if (!baseUrl || !token) throw new Error('NEXORA_BASE_URL and NEXORA_ACCESS_TOKEN are required.');

export const options = {
  scenarios: {
    steady: {
      executor: 'constant-arrival-rate',
      rate: 15,
      timeUnit: '1s',
      duration: '10m',
      preAllocatedVUs: 50,
      maxVUs: 50,
    },
    burst: {
      executor: 'constant-arrival-rate',
      rate: 30,
      timeUnit: '1s',
      duration: '60s',
      startTime: '10m5s',
      preAllocatedVUs: 100,
      maxVUs: 100,
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<500'],
    http_req_failed: ['rate<0.01'],
  },
};

export default function () {
  const response = http.get(`${baseUrl}/api/v1/me`, {
    headers: { Authorization: `Bearer ${token}` },
    tags: { endpoint: 'me' },
  });
  check(response, { 'authenticated CRUD read returns 200': (result) => result.status === 200 });
}
