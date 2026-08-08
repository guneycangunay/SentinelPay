# ADR 008: Preserve provider identity across bounded HTTP retries

## Status

Accepted.

## Context

Retrying a payment mutation can duplicate money movement if the remote endpoint processed the first request. Avoiding all retries leaves recoverable rate limits and transient failures to every caller.

## Decision

SentinelPay persists operation intent before network I/O. The HTTP adapter retries only timeout, `408`, `429`, and `5xx` responses within a small bounded budget. Every attempt uses the same provider idempotency key. Business errors such as decline or invalid capture amount are returned without retry. Repeated exhausted calls open a shared circuit for a short interval; one half-open probe decides whether normal traffic resumes.

If the budget is exhausted, the operation remains `Started`; a later same-key request or reconciliation owns recovery.

## Consequences

- Correctness depends on the configured provider honoring its idempotency contract.
- Retry count is intentionally small to preserve the request latency budget.
- A provider without native idempotency requires lookup/reconciliation and cannot use this policy unchanged.
- The local simulator and adapter contract tests make the behavior reproducible without real credentials.
