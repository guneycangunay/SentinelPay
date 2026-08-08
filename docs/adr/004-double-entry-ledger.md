# ADR 004: Record financial movement in a balanced double-entry ledger

- Status: accepted
- Date: 2026-08-08

## Context

Mutable payment totals show current state but cannot independently explain how funds moved into merchant payable, out through refunds, or into settlement. Reconstructing balances from events or status columns is fragile under retries and corrections.

## Decision

Represent capture, refund, and settlement as immutable journals. Each journal has at least two positive lines and must balance total debits and credits before it can exist. External references are unique so producing the same financial effect twice is harmless.

Use three accounts in this bounded context: `ProviderClearing`, `MerchantPayable`, and `SettlementClearing`. Store integer minor units and one currency per journal.

## Consequences

- Financial movement is explainable and independently checkable.
- Corrections must be compensating journals, not historical edits.
- Reporting can derive balances from ledger lines.
- Fees, reserves, FX, disputes, and payout confirmation require additional accounts and journals rather than flags.
- Ledger retention and database access controls become part of the financial audit boundary.
