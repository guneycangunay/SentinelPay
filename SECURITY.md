# Security policy

## Supported versions

The latest commit on `main` is the supported development version.

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability. Report it privately to the repository owner with:

- affected commit and component;
- reproduction steps or proof of concept;
- expected impact;
- suggested mitigation, if available.

Avoid including real payment data, access tokens, private keys, or customer information.

## Scope and non-goals

SentinelPay is a reference implementation. It is not PCI DSS certified and must not process real cardholder data as provided. The API accepts sandbox payment method tokens only.

Before production use, add at minimum:

- merchant authentication and scoped authorization;
- managed secrets and automated rotation;
- provider-specific webhook timestamp/replay validation;
- encryption and retention policies;
- audited migrations and deployment approvals;
- network isolation and egress controls;
- dependency, container, and source scanning;
- incident response and reconciliation procedures.
