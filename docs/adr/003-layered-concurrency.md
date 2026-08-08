# ADR 003: Use layered concurrency controls

- Status: accepted
- Date: 2026-08-08

## Context

Concurrent capture and refund requests can race across multiple nodes. A distributed lock can expire, a process can pause, and a network can partition.

## Decision

Use Redis leases to reduce duplicate work, provider-native idempotency to protect remote operations, database uniqueness for operation identities, and PostgreSQL optimistic concurrency for final state integrity.

## Consequences

- No single lock is treated as an exactly-once guarantee.
- Retry behavior remains safe after lock expiry or process failure.
- Operations can return a conflict during genuine concurrent updates.
- Provider adapters carry a stronger contract than simple HTTP wrappers.
