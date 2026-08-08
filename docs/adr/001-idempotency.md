# ADR 001: Persist operation intent before remote mutation

- Status: accepted
- Date: 2026-08-08

## Context

Merchants retry requests when responses are lost. The application can also stop after a provider accepts a mutation but before local final state commits. A payment row that appears only after provider success cannot distinguish “never attempted” from “attempted with unknown result.”

## Decision

Every authorize, capture, refund, and reconciliation mutation has a `PaymentOperation` with `Started`, `Succeeded`, or `Failed` status. Merchant operations are unique by `(MerchantId, Type, IdempotencyKey)` and store a canonical SHA-256 request fingerprint.

The application commits a new `Started` operation before invoking the provider. It forwards the same key to the provider. A matching retry resumes `Started`, replays completed results, and rejects a changed fingerprint.

## Consequences

- Ambiguous attempts are durable and queryable.
- Remote mutations require two local commits.
- Real adapters must map operation identity to provider-native idempotency or lookup.
- Request canonicalization is versioned behavior and cannot change silently.
- Completed provider declines are replayed without another provider call.
