#!/usr/bin/env bash
set -euo pipefail

api_base="${SENTINELPAY_API_BASE:-http://localhost:8080}"
api_key="${SENTINELPAY_API_KEY:?SENTINELPAY_API_KEY is required}"
run_id="${1:-$(date +%s)}"

command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }
command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

echo "1/4 Authorizing payment"
payment_json="$(curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: demo-create-${run_id}" \
  -d "{
    \"merchantReference\": \"demo-order-${run_id}\",
    \"amountMinor\": 12990,
    \"currency\": \"EUR\",
    \"provider\": \"mock-bank\",
    \"paymentMethodToken\": \"tok_visa\"
  }")"
payment_id="$(jq -r '.id' <<<"${payment_json}")"
jq '{id, status, amountMinor, currency, providerReference}' <<<"${payment_json}"

echo "2/4 Replaying authorization safely"
curl --fail-with-body --silent --show-error --dump-header - --output /dev/null \
  "${api_base}/api/v1/payments" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: demo-create-${run_id}" \
  -d "{
    \"merchantReference\": \"demo-order-${run_id}\",
    \"amountMinor\": 12990,
    \"currency\": \"EUR\",
    \"provider\": \"mock-bank\",
    \"paymentMethodToken\": \"tok_visa\"
  }" | grep -iE 'HTTP/|Idempotent-Replay'

echo "3/4 Capturing payment"
curl --fail-with-body --silent --show-error -X POST \
  "${api_base}/api/v1/payments/${payment_id}/capture" \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: demo-capture-${run_id}" \
  | jq '{id, status, capturedAmountMinor}'

echo "4/4 Refunding EUR 29.90"
curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments/${payment_id}/refunds" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: demo-refund-${run_id}" \
  -d '{"amountMinor":2990}' \
  | jq '{id, status, refundedAmountMinor, refunds}'

echo "Demo completed for payment ${payment_id}."
