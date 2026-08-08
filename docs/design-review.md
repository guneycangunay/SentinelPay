# Design review guide

This document is a concise map for reviewing or discussing SentinelPay in an architecture interview.

## Thirty-second summary

SentinelPay is a multi-tenant .NET payment reliability service. It models 3DS as an asynchronous payment intent, records remote operation intent before calling a provider, resumes ambiguous attempts with the same idempotency key, supports multiple captures and authorization voids, and writes every financial movement to a balanced double-entry ledger. A transactional outbox, idempotent RabbitMQ consumer, signed webhooks, online and file-based reconciliation, telemetry, load profiles, and chaos drills make failure behavior observable rather than theoretical.

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

### Why is 3DS a state instead of another endpoint result?

The authorization is incomplete but not failed. `RequiresAction` preserves the provider reference, HTTPS next action, expiry, and safe continuation identity. Merchant confirmation and provider webhook completion converge on the same forward transition.

### Why are captures child records if the payment has a captured total?

The total protects the aggregate remainder and supports fast reads. Capture rows preserve operation identity and amount. Ledger journals reference each capture ID, preventing a second partial capture from posting the cumulative amount again.

### Why not automatically repair every report mismatch?

A mismatch can mean a lost callback, wrong reporting window, provider defect, or incorrect currency. The status worker applies narrow authoritative forward repairs; CSV imports preserve wider discrepancies as review evidence.

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
| Consumer commits then loses ACK | Inbox uniqueness detects the CloudEvent ID; redelivery is ACKed without a second effect |
| 3DS browser never returns | Action expiry worker closes the intent and emits an expiry event |
| Two partial captures exceed authorization | Aggregate remainder, operation uniqueness, row version, lease, and provider key reject the loser |
| Provider report changes captured amount | Typed reconciliation issue; no silent ledger rewrite |

## Scaling path

1. Scale stateless API nodes; Redis leases coordinate hot aggregates.
2. Scale outbox workers; `SKIP LOCKED` distributes rows.
3. Partition or archive outbox/inbox tables by time at sustained volume.
4. Add read models for high-volume merchant reporting instead of loading aggregates.
5. Tune provider-specific circuit breaking, timeout budgets, and native status lookup from measured SLAs.
6. Move reconciliation candidates to provider-aware schedules and bounded work queues.
7. Add payout execution and confirmation as a separate state machine, not a flag on settlement.

## Honest trade-offs

- Two commits per remote mutation improve recoverability at the cost of write amplification.
- The request fingerprint is code-defined; changing canonicalization requires versioning.
- Settlement source-line assignment is simple and auditable but would need paging for very large periods.
- API-key hashing uses SHA-256 because keys are high-entropy machine secrets; human passwords would require a slow password KDF.
- The HTTP simulator proves serialization, retry, and provider-contract behavior, not a certified acquirer integration.
- Metrics intentionally avoid merchant/payment IDs to control cardinality; investigations use traces and logs.

## Suggested walkthrough

1. `PaymentService.CreateAsync` — durable intent, replay, provider call, final commit.
2. `Payment` and `PaymentOperation` — state and completion invariants.
3. `LedgerJournal.Create` and `LedgerWriter` — balanced financial movements.
4. `SettlementService` — source selection and atomic assignment.
5. `OutboxDispatcher` and `RabbitMqEventPublisher` — claim/publish/dead-letter contract.
6. `HmacWebhookSignatureVerifier` and `WebhookService` — callback trust and deduplication.
7. `ProviderHttpGateway` and `SentinelPay.ProviderSimulator` — real HTTP failure boundary.
8. `AuditEventConsumer` — inbox-before-ACK ordering and poison-message path.
9. Integration tests — 3DS, partial capture, drift classification, tenant isolation, ambiguous retry, and ledger flow.
10. `make interview` — one executable story across the major decisions.
