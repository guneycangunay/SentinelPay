import http from 'k6/http';
import { check, sleep } from 'k6';
import exec from 'k6/execution';

export const options = {
  scenarios: {
    lifecycle: {
      executor: 'ramping-arrival-rate',
      startRate: 5,
      timeUnit: '1s',
      preAllocatedVUs: 20,
      maxVUs: 100,
      stages: [
        { target: 20, duration: '30s' },
        { target: 50, duration: '1m' },
        { target: 0, duration: '15s' },
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500', 'p(99)<1000'],
    checks: ['rate>0.99'],
  },
};

const baseUrl = __ENV.BASE_URL || 'http://localhost:8080';
const apiKey = __ENV.API_KEY || '${SENTINELPAY_API_KEY}';

function headers(idempotencyKey) {
  return {
    'Content-Type': 'application/json',
    'X-Api-Key': apiKey,
    'Idempotency-Key': idempotencyKey,
  };
}

export default function () {
  const operationId = `${exec.scenario.iterationInTest}-${Date.now()}-${__VU}`;
  const createKey = `k6-create-${operationId}`;
  const create = http.post(
    `${baseUrl}/api/v1/payments`,
    JSON.stringify({
      merchantReference: `load-${operationId}`,
      amountMinor: 12990,
      currency: 'EUR',
      provider: 'mock-bank',
      paymentMethodToken: 'tok_visa',
    }),
    { headers: headers(createKey), tags: { operation: 'authorize' } },
  );

  const created = check(create, {
    'authorization returns 201': (response) => response.status === 201,
    'authorization has payment id': (response) => Boolean(response.json('id')),
  });
  if (!created) return;

  const paymentId = create.json('id');
  const replay = http.post(
    `${baseUrl}/api/v1/payments`,
    create.request.body,
    { headers: headers(createKey), tags: { operation: 'authorize-replay' } },
  );
  check(replay, {
    'replay returns original resource': (response) => response.status === 200,
    'replay is explicitly marked': (response) => response.headers['Idempotent-Replay'] === 'true',
  });

  const capture = http.post(
    `${baseUrl}/api/v1/payments/${paymentId}/capture`,
    null,
    { headers: headers(`k6-capture-${operationId}`), tags: { operation: 'capture' } },
  );
  check(capture, {
    'capture succeeds': (response) => response.status === 200,
    'payment is captured': (response) => response.json('status') === 'Captured',
  });

  sleep(0.1);
}
