#!/usr/bin/env bash
set -euo pipefail

api_base="${SENTINELPAY_API_BASE:-http://localhost:8080}"
api_key="${SENTINELPAY_API_KEY:?SENTINELPAY_API_KEY is required}"
run_id="${1:-$(date +%s)}"

command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }
command -v docker >/dev/null || { echo "docker is required" >&2; exit 1; }

restore_broker() {
  docker compose start rabbitmq >/dev/null 2>&1 || true
}
trap restore_broker EXIT

echo "1/4 Stopping RabbitMQ"
docker compose stop rabbitmq

echo "2/4 Committing a payment while event delivery is unavailable"
curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: chaos-create-${run_id}" \
  -d "{
    \"merchantReference\": \"chaos-order-${run_id}\",
    \"amountMinor\": 4200,
    \"currency\": \"EUR\",
    \"provider\": \"mock-bank\",
    \"paymentMethodToken\": \"tok_visa\"
  }" >/dev/null

pending_before="$(docker compose exec -T postgres psql -U sentinelpay -d sentinelpay -Atc \
  'SELECT count(*) FROM sentinelpay.outbox_messages WHERE "ProcessedAt" IS NULL AND "DeadLetteredAt" IS NULL;')"
echo "Pending outbox messages while broker is down: ${pending_before}"
if [[ "${pending_before}" -lt 1 ]]; then
  echo "Expected at least one pending outbox message." >&2
  exit 1
fi

echo "3/4 Restarting RabbitMQ"
docker compose start rabbitmq

echo "4/4 Waiting for at-least-once delivery recovery"
for attempt in $(seq 1 30); do
  pending_after="$(docker compose exec -T postgres psql -U sentinelpay -d sentinelpay -Atc \
    'SELECT count(*) FROM sentinelpay.outbox_messages WHERE "ProcessedAt" IS NULL AND "DeadLetteredAt" IS NULL;')"
  if [[ "${pending_after}" == "0" ]]; then
    trap - EXIT
    echo "Outbox recovered; all pending messages were published."
    exit 0
  fi
  sleep 2
done

echo "Outbox did not drain within 60 seconds." >&2
exit 1
