# ADR 010: Classify report drift before financial repair

## Status

Accepted.

## Context

Provider settlement reports can disagree with local state because of lost callbacks, wrong report windows, duplicates, currency errors, or genuinely missing transactions. Automatically mutating local money state for every mismatch destroys evidence and can compound an upstream error.

## Decision

CSV imports are strict, bounded, hash-identified, and merchant-scoped. They compare provider reference, authorized amount, captured amount, currency, and state. Mismatches become typed review issues; imports do not rewrite payment or ledger state.

The online status worker remains a separate mechanism and applies only narrow forward repairs that have an authoritative provider status and a valid ledger transition.

## Consequences

- Operational review is explicit and auditable.
- Duplicate source files replay the existing report.
- Report format versioning will be required when providers change schemas.
- Large reports eventually need streamed parsing and partitioned storage; the reference implementation intentionally caps size and row count.
