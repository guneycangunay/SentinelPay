# ADR 005: Resolve merchant identity from scoped, hashed API keys

- Status: accepted
- Date: 2026-08-08

## Context

Accepting a merchant ID in a request would allow callers to choose their tenant boundary. The API needs a simple service-to-service identity mechanism that demonstrates tenant isolation without introducing a separate identity platform.

## Decision

Read a high-entropy key from `X-Api-Key`, hash it with SHA-256, and resolve one non-expired, non-revoked credential and active merchant. Create merchant and scope claims from trusted storage. Require explicit policies per endpoint and take MerchantId only from the authenticated principal.

## Consequences

- Callers cannot select MerchantId in the request contract.
- Plaintext keys are unavailable after issuance.
- Credential lookup requires an indexed hash.
- Production needs issuance, rotation, revocation audit, secret delivery, and abuse detection workflows.
- Human passwords must not reuse this fast-hash design; API keys are assumed to be high entropy.
