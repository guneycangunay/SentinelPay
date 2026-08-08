#!/usr/bin/env bash
set -euo pipefail

api_base="${API_BASE:-http://localhost:8080}"
api_key="${SENTINELPAY_API_KEY:?SENTINELPAY_API_KEY is required}"
run_id="${RUN_ID:-$(date +%s)}"
report_file="$(mktemp)"
trap 'rm -f -- "${report_file}"' EXIT

echo "1/6 Creating a provider-backed payment that requires 3DS"
payment_json="$(curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: interview-create-${run_id}" \
  -d "{
    \"merchantReference\": \"interview-order-${run_id}\",
    \"amountMinor\": 10000,
    \"currency\": \"EUR\",
    \"provider\": \"acquirer-http\",
    \"paymentMethodToken\": \"tok_http_3ds\"
  }")"
payment_id="$(jq -r '.id' <<<"${payment_json}")"
provider_reference="$(jq -r '.providerReference' <<<"${payment_json}")"
jq '{id, status, nextAction}' <<<"${payment_json}"

echo "2/6 Confirming the cardholder challenge"
curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments/${payment_id}/confirm" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: interview-confirm-${run_id}" \
  -d '{"authenticationResultToken":"auth_success"}' \
  | jq '{id, status, authorizationExpiresAt}'

echo "3/6 Capturing EUR 40.00 and preserving the authorization remainder"
curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments/${payment_id}/capture" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: interview-capture-${run_id}" \
  -d '{"amountMinor":4000}' \
  | jq '{status, capturedAmountMinor, remainingAuthorizedAmountMinor, captures}'

echo "4/6 Voiding the remaining EUR 60.00 authorization"
curl --fail-with-body --silent --show-error -X POST \
  "${api_base}/api/v1/payments/${payment_id}/void" \
  -H "X-Api-Key: ${api_key}" \
  -H "Idempotency-Key: interview-void-${run_id}" \
  | jq '{status, capturedAmountMinor, voidedAmountMinor, remainingAuthorizedAmountMinor}'

echo "5/6 Importing a deliberately inconsistent provider report"
occurred_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
period_start="$(jq -nr 'now - 3600 | todateiso8601 | @uri')"
period_end="$(jq -nr 'now + 3600 | todateiso8601 | @uri')"
{
  echo 'provider_reference,authorized_amount_minor,captured_amount_minor,currency,state,occurred_at'
  echo "${provider_reference},10000,4500,EUR,captured,${occurred_at}"
} >"${report_file}"
curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/reconciliation/imports/acquirer-http?periodStart=${period_start}&periodEnd=${period_end}" \
  -H 'Content-Type: text/csv' \
  -H "X-Api-Key: ${api_key}" \
  -H "X-Report-Name: interview-provider-${run_id}.csv" \
  --data-binary "@${report_file}" \
  | jq '{status, providerRowCount, matchedRowCount, issues}'

echo "6/6 Reading the final aggregate and durable operation history"
curl --fail-with-body --silent --show-error \
  "${api_base}/api/v1/payments/${payment_id}" \
  -H "X-Api-Key: ${api_key}" \
  | jq '{id, status, capturedAmountMinor, voidedAmountMinor, operations}'

echo "Interview path completed for ${payment_id}."
