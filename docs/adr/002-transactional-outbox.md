# ADR 002: Use a claimed transactional outbox with at-least-once delivery

- Status: accepted
- Date: 2026-08-08

## Context

Payment state and event publication span PostgreSQL and RabbitMQ. Publishing first can expose state that never commits; committing first can lose an event. Holding database locks during broker I/O reduces throughput and makes failure recovery harder.

## Decision

Persist final payment/finance changes and outbox intent in one PostgreSQL unit of work. Dispatchers claim small batches in a short transaction with `FOR UPDATE SKIP LOCKED` and a claim expiry, then publish outside the transaction.

Messages are persistent CloudEvents sent with mandatory routing and publisher confirms. Successful rows receive `ProcessedAt`. Failures use bounded exponential backoff and receive `DeadLetteredAt` after a configured attempt limit.

## Consequences

- Database state and event intent are atomic.
- Multiple dispatchers can partition work without a global mutex.
- Publication is at least once; consumers must deduplicate by event ID.
- A publish followed by a local commit failure can produce a duplicate.
- Pending age, failure rate, and dead-letter count become required operational signals.
- Processed rows require an archive/retention policy at production volume.
