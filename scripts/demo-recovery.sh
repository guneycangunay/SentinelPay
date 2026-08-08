#!/usr/bin/env bash
set -euo pipefail

api_base="${SENTINELPAY_API_BASE:-http://localhost:8080}"
api_key="${SENTINELPAY_API_KEY:?SENTINELPAY_API_KEY is required}"
run_id="${1:-$(date +%s)}"
idempotency_key="recovery-create-${run_id}"
payload="{
  \"merchantReference\": \"recovery-order-${run_id}\",
  \"amountMinor\": 7500,
  \"currency\": \"EUR\",
  \"provider\": \"mock-bank\",
  \"paymentMethodToken\": \"tok_transient_once\"
}"

command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }
command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

echo "1/2 Simulating a provider connection loss after the operation is persisted"
first_status="$(curl --silent --output /tmp/sentinelpay-recovery-first.json --write-out '%{http_code}' \
  "${api_base}/api/v1/payments" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: ${idempotency_key}" \
  -d "${payload}")"
if [[ "${first_status}" != "503" ]]; then
  jq . /tmp/sentinelpay-recovery-first.json
  echo "Expected the first request to return 503, received ${first_status}." >&2
  exit 1
fi
jq '{title, status, detail, traceId}' /tmp/sentinelpay-recovery-first.json

echo "2/2 Retrying with the same key and resuming the persisted operation"
curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: ${idempotency_key}" \
  -d "${payload}" \
  | jq '{id, status, providerReference, operations}'
