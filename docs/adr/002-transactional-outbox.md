# ADR 002: Use a transactional outbox for integration events

- Status: accepted
- Date: 2026-08-08

## Context

Writing payment state and publishing an event are two separate operations. Publishing first can expose a state that never commits; committing first can lose an event if the process crashes.

## Decision

Persist payment changes and outbox messages in one PostgreSQL transaction. A background dispatcher publishes pending messages and marks them processed afterward.

## Consequences

- Database state and event intent are atomic.
- Publication is at least once, so consumers must deduplicate.
- Outbox lag must be monitored and old processed rows must be archived.
- A broker-specific publisher can be introduced without changing payment use cases.
