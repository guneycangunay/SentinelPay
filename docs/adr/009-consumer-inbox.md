# ADR 009: Commit consumer inbox before broker acknowledgement

## Status

Accepted.

## Context

The outbox can publish the same CloudEvent more than once. A consumer can also commit its database work and stop before RabbitMQ receives its acknowledgement.

## Decision

The audit consumer uses manual acknowledgements and a unique `(Consumer, EventId)` PostgreSQL inbox. It commits the inbox-owned audit side effect before ACK. A redelivery of a committed event is acknowledged without another insert. Malformed envelopes are rejected to a dead-letter queue; transient processing failures are requeued.

## Consequences

- Transport remains at least once; local database effects are idempotent.
- Each new consumer must define its own consumer name and transactional side effect boundary.
- Inbox retention and archive policy are required at sustained production volume.
- External side effects outside PostgreSQL need their own idempotency contract.
