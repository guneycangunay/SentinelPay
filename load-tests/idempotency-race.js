import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  scenarios: {
    duplicate_race: {
      executor: 'per-vu-iterations',
      vus: 25,
      iterations: 1,
      maxDuration: '30s',
    },
  },
  thresholds: {
    checks: ['rate==1'],
  },
};

const baseUrl = __ENV.BASE_URL || 'http://localhost:8080';
const apiKey = __ENV.API_KEY;
const raceId = __ENV.RACE_ID || 'local-race';

if (!apiKey) {
  throw new Error('API_KEY environment variable is required.');
}

export function setup() {
  for (let attempt = 0; attempt < 30; attempt += 1) {
    const response = http.get(`${baseUrl}/health/ready`);
    if (response.status === 200) return;
    sleep(2);
  }

  throw new Error('SentinelPay did not become ready.');
}

export default function () {
  const response = http.post(
    `${baseUrl}/api/v1/payments`,
    JSON.stringify({
      merchantReference: `race-order-${raceId}`,
      amountMinor: 4200,
      currency: 'EUR',
      provider: 'mock-bank',
      paymentMethodToken: 'tok_visa',
    }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-Api-Key': apiKey,
        'Idempotency-Key': `race-create-${raceId}`,
      },
    },
  );

  check(response, {
    'one creation or a deterministic replay': (result) => result.status === 201 || result.status === 200,
    'response contains payment id': (result) => Boolean(result.json('id')),
  });
}
