#!/usr/bin/env bash
set -euo pipefail

environment_file="${1:-.env}"

if [[ -e "${environment_file}" ]]; then
  echo "Using existing ${environment_file}; no credentials were changed."
  exit 0
fi

command -v openssl >/dev/null || {
  echo "openssl is required to generate local credentials." >&2
  exit 1
}

umask 077
{
  echo "SENTINELPAY_API_BASE=http://localhost:8080"
  echo "SENTINELPAY_API_KEY=sp_test_$(openssl rand -hex 24)"
  echo "SENTINELPAY_DB_PASSWORD=$(openssl rand -hex 24)"
  echo "SENTINELPAY_RABBITMQ_PASSWORD=$(openssl rand -hex 24)"
  echo "SENTINELPAY_MOCK_BANK_WEBHOOK_SECRET=$(openssl rand -hex 32)"
  echo "SENTINELPAY_SANDBOX_WALLET_WEBHOOK_SECRET=$(openssl rand -hex 32)"
  echo "SENTINELPAY_ACQUIRER_WEBHOOK_SECRET=$(openssl rand -hex 32)"
  echo "SENTINELPAY_GRAFANA_ADMIN_PASSWORD=$(openssl rand -hex 24)"
} > "${environment_file}"

echo "Generated local credentials in ${environment_file}. Keep this file private."
