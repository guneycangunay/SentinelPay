# Design review guide

This document is a concise map for reviewing or discussing SentinelPay in an architecture interview.

## Thirty-second summary

SentinelPay is a multi-tenant .NET payment reliability service. It records remote operation intent before calling a provider, resumes ambiguous attempts with the same idempotency key, writes captures/refunds/settlements to a balanced double-entry ledger, commits integration-event intent transactionally, and delivers those events through a horizontally scalable RabbitMQ outbox. Timestamped webhooks, tenant-scoped API keys, reconciliation, telemetry, load profiles, and chaos drills make failure behavior observable rather than theoretical.

## Decisions worth defending

### Why commit an operation before the provider call?

Without that record, a crash can erase whether SentinelPay ever attempted the call. `Started` is a durable recovery state. It does introduce two database commits per external mutation, but that cost buys explicit ambiguity handling and operator visibility.

### Why not hold one database transaction around the provider call?

Remote latency would hold locks and connections for an unbounded interval while still failing to atomically commit the provider's system. The architecture uses short local transactions and provider idempotency instead of pretending a distributed transaction exists.

### Why Redis plus PostgreSQL concurrency?

Redis reduces duplicate remote calls under normal contention. It cannot be the only correctness boundary because leases expire and networks partition. Unique constraints, row versions, operation hashes, and aggregate transitions remain authoritative.

### Why a ledger if the payment already stores totals?

Payment totals answer current state; the ledger explains every financial movement and provides a settlement basis. Immutable balanced journals make reconciliation and audit possible without reconstructing history from mutable columns.

### Why at-least-once events?

Publishing and marking a row processed cannot be made atomic across PostgreSQL and RabbitMQ without a distributed protocol. The practical contract is durable intent plus retry, with stable event IDs and consumer deduplication.

### Why API keys instead of JWT/OAuth?

The sample models service-to-service merchant access with a narrow credential surface. Keys are hashed, scoped, expirable, and revocable. A production control plane could replace authentication while preserving merchant claims and authorization policies.

## Failure questions

| Question | Answer to locate in the code |
|---|---|
| Provider accepted but API crashed | `PaymentOperation.Status == Started`; retry calls adapter with same key |
| RabbitMQ is down for 20 minutes | Payment commits; outbox backs off; chaos drill verifies eventual drain |
| Two API nodes capture simultaneously | Merchant/payment lease; unique operation key; payment row version; state transition |
| Same webhook arrives 50 times | Valid HMAC; first receipt commits; remaining calls replay from inbox identity |
| One reconciliation item throws | Per-payment scope/catch continues the remaining batch |
| Refund exceeds remaining amount | Domain validation happens before provider invocation |
| A merchant guesses another UUID | Authenticated MerchantId is part of the storage predicate; returns 404 |
| Worker publishes then crashes | Event may be duplicated; CloudEvent ID is stable for consumer deduplication |

## Scaling path

1. Scale stateless API nodes; Redis leases coordinate hot aggregates.
2. Scale outbox workers; `SKIP LOCKED` distributes rows.
3. Partition or archive outbox/inbox tables by time at sustained volume.
4. Add read models for high-volume merchant reporting instead of loading aggregates.
5. Introduce provider-specific circuit breaking, timeout budgets, and native status lookup.
6. Move reconciliation candidates to provider-aware schedules and bounded work queues.
7. Add payout execution and confirmation as a separate state machine, not a flag on settlement.

## Honest trade-offs

- Two commits per remote mutation improve recoverability at the cost of write amplification.
- The request fingerprint is code-defined; changing canonicalization requires versioning.
- Settlement source-line assignment is simple and auditable but would need paging for very large periods.
- API-key hashing uses SHA-256 because keys are high-entropy machine secrets; human passwords would require a slow password KDF.
- The sandbox adapter proves orchestration behavior, not a provider SDK's edge cases.
- Metrics intentionally avoid merchant/payment IDs to control cardinality; investigations use traces and logs.

## Suggested walkthrough

1. `PaymentService.CreateAsync` — durable intent, replay, provider call, final commit.
2. `Payment` and `PaymentOperation` — state and completion invariants.
3. `LedgerJournal.Create` and `LedgerWriter` — balanced financial movements.
4. `SettlementService` — source selection and atomic assignment.
5. `OutboxDispatcher` and `RabbitMqEventPublisher` — claim/publish/dead-letter contract.
6. `HmacWebhookSignatureVerifier` and `WebhookService` — callback trust and deduplication.
7. Integration tests — tenant isolation, ambiguous retry, webhook expiry, ledger/settlement flow.
8. `scripts/` and `observability/` — executable operational evidence.
