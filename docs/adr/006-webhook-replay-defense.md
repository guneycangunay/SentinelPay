# ADR 006: Authenticate webhook timestamp and exact body

- Status: accepted
- Date: 2026-08-08

## Context

A body-only HMAC proves authenticity but allows anyone who captures a valid request to replay it indefinitely. Providers also legitimately redeliver events, so rejecting every repeated request at the edge is not viable.

## Decision

Use `t=<unix-seconds>,v1=<hex-hmac>` and sign `{timestamp}.{exact raw body}` with a provider-specific HMAC-SHA256 secret. Reject timestamps outside a configurable tolerance and compare bytes in constant time. After authentication, deduplicate valid deliveries with a unique `(provider, eventId)` inbox receipt.

## Consequences

- Captured requests have a bounded replay window.
- Legitimate redelivery inside the window remains safe.
- Provider and service clocks must be synchronized.
- Secret rotation needs an overlap strategy in a real adapter.
- Signature parsing and canonicalization are provider contract details and require compatibility tests.
