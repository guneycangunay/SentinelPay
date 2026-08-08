# Changelog

All notable changes are documented here. The project follows semantic versioning for repository milestones.

## [2.1.0] - 2026-08-08

### Added

- Payment-intent states for 3DS/SCA challenge, confirmation, action expiry, and authorization expiry.
- Multiple partial captures, explicit authorization remainder, and full/partial void behavior.
- Capture entities and per-capture double-entry journals, avoiding cumulative ledger duplication.
- A real HTTP provider adapter with timeout budget, bounded retry, `Retry-After`, business-error mapping, and stable provider idempotency keys.
- A standalone provider simulator covering authorization, 3DS, capture, void, refund, rate limiting, timeout, and status lookup contracts.
- RabbitMQ audit consumer with manual acknowledgements, PostgreSQL inbox uniqueness, poison-message dead lettering, and commit-before-ACK ordering.
- CSV reconciliation imports that classify missing, amount, currency, and state mismatches without silently changing money state.
- Authorization-expiry worker, HTTP contract tests, multi-capture and 3DS lifecycle tests, and an interview demo script.

### Changed

- Capture requests now carry an explicit integer minor-unit amount.
- Provider operations expose provider-side capture and void references.
- Automatic reconciliation can repair incremental provider captures without posting cumulative ledger totals twice.
- Local database, broker, API, webhook, and Grafana credentials are generated into a git-ignored `.env`; no runnable credential is committed.
- Repository version advanced to `2.1.0`.

## [2.0.0] - 2026-08-08

### Added

- Merchant-scoped, hashed API-key authentication with route-level scopes and tenant isolation.
- Durable payment operation ledger for crash-safe authorize, capture, refund, and reconciliation attempts.
- Balanced double-entry journals for capture, refund, and settlement movements.
- Idempotent settlement batches over unassigned merchant-payable lines.
- RabbitMQ CloudEvent publisher with durable topology, mandatory routing, and publisher confirms.
- Multi-worker outbox claiming with `SKIP LOCKED`, claim expiry, backoff, and dead-letter threshold.
- Timestamped webhook HMAC verification and replay-window enforcement.
- Stateful sandbox provider controls for transient failure and reconciliation drills.
- Reviewed PostgreSQL migration and development-only merchant seed.
- Payment/provider/outbox telemetry, Prometheus, provisioned Grafana dashboard, k6 profiles, and chaos scripts.
- Tenant-isolation, operation-recovery, webhook-expiry, ledger, and settlement tests.
- CodeQL and production container build gates.

### Changed

- Payment reads and mutations now require authenticated merchant context.
- Provider operations persist intent before remote invocation and complete with state, ledger, and outbox changes.
- Reconciliation commits each payment independently so one failure does not roll back a batch.
- Startup schema management uses EF migrations rather than `EnsureCreated`.

## [1.0.0] - 2026-08-08

### Added

- Provider-independent authorize, capture, refund, webhook, idempotency, outbox, reconciliation, telemetry, Docker, and test foundations.
