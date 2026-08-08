# ADR 001: Persist payment idempotency keys and request hashes

- Status: accepted
- Date: 2026-08-08

## Context

Merchants retry requests when connections time out. A retry may arrive at another application instance after the first provider call succeeded.

## Decision

Every mutating merchant operation requires an idempotency key. Create operations store the key and a canonical SHA-256 request fingerprint with the payment. Refunds store both on the refund row. A matching retry returns the existing resource; a different fingerprint returns a conflict.

The same key is forwarded to provider adapters.

## Consequences

- Safe retries become an explicit part of the public contract.
- Keys require retention and uniqueness policy.
- Provider adapters must preserve native idempotency semantics.
- Payload canonicalization becomes versioned behavior and must not change silently.
