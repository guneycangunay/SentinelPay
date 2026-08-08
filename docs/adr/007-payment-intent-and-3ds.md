# ADR 007: Model 3DS as payment-intent state

## Status

Accepted.

## Context

Card authorization can require cardholder action. Treating this as a failed synchronous request loses the provider reference, challenge expiry, and safe continuation identity.

## Decision

The payment aggregate exposes `RequiresAction` with an HTTPS next action and expiry. Confirmation is a durable, idempotent operation. Successful confirmation moves to `Authorized`; failure or expiry closes the intent. Provider webhooks may complete the same forward transition.

Only tokenized payment and authentication results cross the API. Challenge data is bounded and the action URL must be absolute HTTPS.

## Consequences

- Merchants must treat payment creation as a stateful intent, not a boolean result.
- Challenge and authorization expiries are explicit operational work.
- The aggregate grows, but transition rules remain in one place.
- Provider adapters must map native challenge contracts into the common next-action shape.
