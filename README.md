<p align="center">
  <img src="docs/sentinelpay-banner.svg" alt="SentinelPay — reliable payment orchestration" width="100%" />
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" />
  <img alt="PostgreSQL 18" src="https://img.shields.io/badge/PostgreSQL-18-4169E1?style=flat-square&logo=postgresql&logoColor=white" />
  <img alt="RabbitMQ 4" src="https://img.shields.io/badge/RabbitMQ-4-FF6600?style=flat-square&logo=rabbitmq&logoColor=white" />
  <img alt="OpenTelemetry" src="https://img.shields.io/badge/OpenTelemetry-instrumented-7B61FF?style=flat-square" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-2dd4bf?style=flat-square" />
</p>

SentinelPay is a production-minded, multi-tenant payment reliability platform. It focuses on the hard parts of money movement: ambiguous provider outcomes, concurrent retries, ledger correctness, durable event delivery, webhook replay attacks, settlement boundaries, and state drift.

The repository uses deterministic sandbox gateways, so the full authorize → capture → refund → settlement lifecycle runs locally without real payment credentials or cardholder data.

## Engineering depth

| Capability | Implementation |
|---|---|
| Crash-safe payment operations | A durable `Started/Succeeded/Failed` operation record is committed before each provider mutation. A retry resumes the same operation and forwards the same key. |
| Tenant isolation | Hashed API keys resolve a merchant identity and scopes; every merchant-owned query is tenant-filtered. |
| Financial correctness | Immutable, balanced double-entry journals track provider clearing, merchant payable, refunds, and settlement clearing. |
| Durable event delivery | Payment state and outbox intent share one PostgreSQL commit; workers claim with `FOR UPDATE SKIP LOCKED` and publish persistent CloudEvents to RabbitMQ with confirms. |
| Failure containment | Bounded exponential retry, claim expiry, dead-letter threshold, per-payment reconciliation isolation, and RFC 9457-style errors. |
| Provider callback security | Timestamped HMAC-SHA256 signatures, constant-time comparison, five-minute replay window, and a deduplicating webhook inbox. |
| Operability | Prometheus metrics, OpenTelemetry traces, JSON logs, Grafana dashboard, health probes, load profiles, and executable recovery/chaos drills. |
| Delivery discipline | Reviewed EF migration, Testcontainers integration suite, CodeQL, dependency updates, coverage artifact, and production container build in CI. |

## System shape

```mermaid
flowchart TB
    Client["Merchant client"] --> API["Authenticated API"]
    Provider["Provider webhook"] --> API
    API --> Core["Payment and finance use cases"]
    Core --> Lock["Redis leases"]
    Core --> Gateway["Provider adapters"]
    Core --> DB[("PostgreSQL")]
    DB --> Worker["Outbox and reconciliation workers"]
    Worker --> Gateway
    Worker --> Broker["RabbitMQ"]
```

The dependency direction remains strict: Domain has no infrastructure dependency; Application owns use cases and ports; Infrastructure implements persistence, locking, messaging, and gateways; API owns transport and identity.

See [the architecture deep dive](docs/architecture.md) for transaction boundaries, failure windows, data ownership, and sequence diagrams.

## Reliability contract

| Failure | Observable behavior |
|---|---|
| Client retries an unchanged request | The original resource is returned with `Idempotent-Replay: true`. |
| Client reuses a key for different data | `409 idempotency-conflict`; no provider call is attempted. |
| API stops after recording intent but before the provider call | The next request resumes the `Started` operation. |
| Provider succeeds but the final database commit is lost | The retry uses the same provider key and converges on the same provider reference. |
| Event broker is unavailable | Business state commits; the outbox retries independently and dead-letters after the configured limit. |
| Two workers claim pending events | PostgreSQL row locks and `SKIP LOCKED` partition work without holding a transaction over network I/O. |
| Provider sends a duplicate webhook | The `(provider, eventId)` inbox constraint makes the replay harmless. |
| Provider state differs from local state | Reconciliation applies only forward repairs and commits each payment independently. |

SentinelPay promises at-least-once event delivery, not exactly-once processing. Consumers must deduplicate on the CloudEvent `id`.

## Run locally

Prerequisite: Docker with Compose v2.

```bash
make up
curl http://localhost:8080/health/ready
```

Local endpoints:

- Swagger: <http://localhost:8080/swagger>
- API: <http://localhost:8080>
- RabbitMQ management: <http://localhost:15672> (`sentinelpay` / `${SENTINELPAY_LOCAL_PASSWORD}`)
- Metrics: <http://localhost:8080/metrics>

The development merchant is seeded by the migration initializer. Its sandbox API key is:

```text
${SENTINELPAY_API_KEY}
```

All credentials in this repository are local-only fixtures. Non-development environments must inject secrets and keep `DevelopmentMerchant:Seed=false`.

## Demonstrate the hard paths

```bash
make demo       # authorize, deterministic replay, capture, partial refund
make recovery   # fail once after operation persistence, then resume safely
make chaos      # stop RabbitMQ, commit payment, restore broker, verify drain
make race       # 25 concurrent requests sharing one idempotency key
```

The standard lifecycle script prints payment state and its operation history. The recovery drill intentionally returns `503` once, then succeeds with the exact same request and idempotency key.

## Observability stack

```bash
make observability
```

Grafana is available at <http://localhost:3000> (`admin` / `${SENTINELPAY_LOCAL_PASSWORD}`) with a provisioned SentinelPay Reliability Overview dashboard. Prometheus is available at <http://localhost:9090>.

Custom signals include authorization outcomes, captured/refunded volume, provider latency, outbox publish failures, and dead-letter counts. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces to an OTLP collector.

## API and authorization

Merchant endpoints require `X-Api-Key`. Mutations also require an `Idempotency-Key` of 8–128 characters.

| Scope | Method and route | Purpose |
|---|---|---|
| `payments:write` | `POST /api/v1/payments` | Authorize a payment |
| `payments:read` | `GET /api/v1/payments/{id}` | Read payment and operation history |
| `payments:write` | `POST /api/v1/payments/{id}/capture` | Full capture |
| `payments:write` | `POST /api/v1/payments/{id}/refunds` | Partial or full refund |
| `payments:read` | `GET /api/v1/providers` | List provider adapters |
| `ledger:read` | `GET /api/v1/ledger/balances?currency=EUR` | Read account balances |
| `ledger:read` | `GET /api/v1/ledger/journals` | Read recent journal metadata |
| `settlements:write` | `POST /api/v1/settlements` | Move payable funds into settlement clearing |
| `settlements:read` | `GET /api/v1/settlements/{id}` | Read a settlement batch |
| anonymous + HMAC | `POST /api/v1/webhooks/{provider}` | Receive a provider event |

Example authorization:

```bash
curl -i http://localhost:8080/api/v1/payments \
  -H 'Content-Type: application/json' \
  -H 'X-Api-Key: ${SENTINELPAY_API_KEY}' \
  -H 'Idempotency-Key: create-order-2026-0001' \
  -d '{
    "merchantReference": "order-2026-0001",
    "amountMinor": 12990,
    "currency": "EUR",
    "provider": "mock-bank",
    "paymentMethodToken": "tok_visa"
  }'
```

## Double-entry ledger

Every journal must contain at least two positive lines and satisfy total debits = total credits. The domain rejects an unbalanced journal before persistence.

| Event | Debit | Credit |
|---|---|---|
| Capture €129.90 | Provider clearing 12,990 | Merchant payable 12,990 |
| Refund €29.90 | Merchant payable 2,990 | Provider clearing 2,990 |
| Settle €100.00 | Merchant payable 10,000 | Settlement clearing 10,000 |

The balance API exposes debits, credits, and `netMinor = credits - debits`. Amounts are always integer minor units; floating-point money is never used.

Settlement creation locks by merchant and currency, selects unassigned payable lines through the requested period end, assigns them to one batch, writes the settlement journal, and emits its outbox event in one unit of work. The sample stops at settlement clearing; it does not pretend to execute a real bank payout.

## Sandbox provider behavior

| Provider | Token | Result |
|---|---|---|
| `mock-bank` | `tok_visa` or normal token | Authorized |
| `mock-bank` | `tok_declined` | `card_declined` |
| `mock-bank` | `tok_insufficient_funds` | `insufficient_funds` |
| `mock-bank` | `tok_transient_once` | First request `503`, same-key retry succeeds |
| `mock-bank` | `tok_timeout` | Retryable provider timeout |
| `sandbox-wallet` | normal token | Authorized |
| `sandbox-wallet` | `wallet_locked` | `wallet_locked` |

Provider references are deterministic from operation identity. The API accepts tokenized payment identifiers only and never accepts PAN or CVV.

## Webhook signing

The signature header follows `t=<unix-seconds>,v1=<hex-hmac>`. The signed bytes are:

```text
{timestamp}.{exact UTF-8 request body}
```

Sign with the provider-specific HMAC-SHA256 secret. Signatures outside `Webhooks:SignatureToleranceSeconds` are rejected before JSON parsing or database access. Inbox uniqueness protects against repeated valid deliveries inside the window.

## Test and quality gates

```bash
dotnet restore SentinelPay.slnx
dotnet build SentinelPay.slnx --configuration Release --no-restore
dotnet test SentinelPay.slnx --configuration Release --no-build
```

The suite covers domain state transitions, operation completion rules, double-entry balance invariants, API authentication, tenant isolation, request replay/conflict behavior, transient operation recovery, webhook deduplication/expiry, ledger movement, and settlement. Integration tests start an isolated PostgreSQL 18 container with Testcontainers and apply the real migration.

Load profiles:

```bash
make load
make race
```

CI builds with warnings as errors, runs tests and coverage, builds the production image, and performs scheduled CodeQL analysis. Dependabot tracks NuGet, GitHub Actions, and container dependencies.

## Operational and design documentation

- [Architecture deep dive](docs/architecture.md)
- [Production runbook](docs/runbook.md)
- [Threat model](docs/threat-model.md)
- [Design review / interview guide](docs/design-review.md)
- [Architecture decision records](docs/adr)
- [Security policy](SECURITY.md)
- [Changelog](CHANGELOG.md)

## Deliberate boundaries

- Full capture only; refunds may be partial or full.
- Gateways are deterministic adapters, not integrations with real financial institutions.
- Settlement records accounting intent; no ACH/SEPA payout rail is implemented.
- API keys are appropriate for this service-to-service sandbox. A production control plane would add key issuance/rotation workflows, managed secrets, audit logs, and possibly mTLS or workload identity.
- Outbox delivery is at least once. Consumer idempotency remains mandatory.
- This is an architecture reference, not a PCI DSS-certified payment processor.

## Repository map

```text
.
├── src/                         Domain, Application, Infrastructure, API
├── tests/                       Domain and PostgreSQL integration tests
├── docs/adr/                    Decision records
├── load-tests/                  Lifecycle and idempotency-race k6 profiles
├── observability/               Prometheus and provisioned Grafana dashboard
├── scripts/                     Demo, recovery, and chaos drills
├── compose.yml                  API, PostgreSQL, Redis, RabbitMQ, optional k6
├── compose.observability.yml    Prometheus and Grafana overlay
├── Dockerfile                   Non-root, read-only compatible runtime image
└── SentinelPay.slnx
```

## License

SentinelPay is available under the [MIT License](LICENSE).
