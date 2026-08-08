# Security policy

## Supported version

The latest commit on `main` is the supported development version.

## Report a vulnerability

Do not open a public issue for a suspected vulnerability. Report it privately to the repository owner and include:

- affected commit and component;
- reproduction steps or a minimal proof of concept;
- expected confidentiality, integrity, or availability impact;
- suggested mitigation, if known.

Do not include real payment data, access tokens, private keys, customer information, or production endpoints.

## Implemented safeguards

- Merchant API keys are stored as SHA-256 hashes and can expire or be revoked.
- Endpoint policies require explicit payment, ledger, or settlement scopes.
- Merchant identity comes from authentication, never from request data.
- Timestamped provider HMAC signatures are compared in constant time and deduplicated through an inbox.
- Idempotency fingerprints reject same-key/different-payload mutations.
- Tokenized payment method identifiers are not persisted as plaintext.
- PostgreSQL constraints and domain invariants protect tenant, operation, payment, and ledger integrity.
- Containers run as a non-root user and Compose mounts the API root filesystem read-only.
- CodeQL, dependency updates, warnings-as-errors builds, tests, and container builds run in GitHub workflows.

See [the threat model](docs/threat-model.md) for trust boundaries, abuse cases, mitigations, and residual risk.

## Deployment responsibilities

SentinelPay is a reference implementation, not a PCI DSS-certified processor. It must not receive real PAN or CVV data. Before production use, provide at minimum:

- managed secrets, rotation, and audited credential issuance;
- TLS at ingress and protected dependency connections;
- workload identity or mTLS where appropriate;
- strict database roles and immutable financial audit controls;
- network isolation and provider-specific egress policy;
- API gateway/WAF controls, quotas, request limits, and abuse detection;
- reviewed migrations, backups, point-in-time recovery, and restore drills;
- log redaction, encryption, retention, privacy, and incident-response policy;
- provider-specific idempotency, signature, timeout, and status-lookup behavior;
- security review and the applicable compliance program.

Development credentials in Compose and `appsettings.Development.json` are public fixtures and must never be reused outside an isolated local environment.
