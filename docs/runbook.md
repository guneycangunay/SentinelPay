# SentinelPay production runbook

This runbook describes the operational intent of the reference implementation. Adapt thresholds, ownership, and commands to the deployed platform before using it in production.

## Service signals

| Signal | Why it matters | Initial alert suggestion |
|---|---|---|
| API 5xx rate | Local defects or dependency failure | >1% for 5 minutes |
| Provider p95 latency by operation | Upstream degradation and timeout risk | Above provider SLO for 10 minutes |
| Authorization failure ratio | Issuer/provider change or abuse | Significant deviation from seven-day baseline |
| Outbox publish failures | Broker or routing failure | Any sustained rate for 5 minutes |
| Dead-letter counter | Event intent requires intervention | Any increase |
| Oldest pending outbox age | Delivery objective is being missed | >5 minutes |
| Stale authorized payments | Local/provider state may be drifting | Growth across three reconciliation cycles |
| Reconciliation correction count | Detects provider callback loss | Significant deviation from baseline |

Metric names and the starter dashboard live in `observability/`. Production alerting should add a database-derived oldest-message gauge and business-volume baselines.

## Health probes

- `/health/live` proves the process can serve HTTP. Do not attach dependency checks to liveness.
- `/health/ready` proves PostgreSQL connectivity. Remove the instance from traffic when it fails.
- RabbitMQ and Redis degradation should not automatically kill the API. The outbox tolerates broker downtime; database constraints and provider idempotency remain correctness barriers if Redis is unavailable, although the current wiring requires Redis connectivity for a merchant mutation.

## Incident: RabbitMQ unavailable

Expected behavior: payment commits continue, outbox publish attempts fail, rows back off, and the API remains available.

1. Confirm broker health and network/DNS reachability.
2. Check `sentinelpay.outbox.failed` and application logs by `MessageId`.
3. Verify pending and dead-letter counts:

```sql
SELECT
    count(*) FILTER (WHERE "ProcessedAt" IS NULL AND "DeadLetteredAt" IS NULL) AS pending,
    count(*) FILTER (WHERE "DeadLetteredAt" IS NOT NULL) AS dead_lettered,
    min("OccurredAt") FILTER (WHERE "ProcessedAt" IS NULL AND "DeadLetteredAt" IS NULL) AS oldest_pending
FROM sentinelpay.outbox_messages;
```

4. Restore the broker or routing topology. Workers reclaim messages after `LockedUntil` and retry after `NextAttemptAt`.
5. Confirm the pending count drains and the RabbitMQ audit queue receives CloudEvents.
6. For dead-lettered rows, identify and correct the root cause before an audited replay. Do not bulk-reset rows while the cause is unknown.

The local `make chaos` drill exercises this path.

## Incident: Redis coordination unavailable

Expected behavior: merchant mutations return retryable `503 coordination-unavailable`; reads and PostgreSQL readiness remain available.

1. Check Redis connectivity, authentication, memory pressure, and latency.
2. Do not bypass coordination by disabling Redis on live instances. Although database/provider barriers remain, changing lock mode during an incident increases simultaneous remote calls.
3. Restore Redis and retry identical requests with identical idempotency keys.
4. Failed lease release is non-fatal: the random-token compare-and-delete may log a warning, and the lease expires through its TTL.
5. Review provider call rate and database concurrency conflicts during the outage window.

## Incident: provider timeout or connection loss

Expected behavior: the API returns retryable `503`, while the operation remains `Started`.

1. Ask the merchant to retry the identical payload with the identical idempotency key.
2. Locate the payment operation:

```sql
SELECT "PaymentId", "Type", "Status", "IdempotencyKey", "StartedAt", "UpdatedAt"
FROM sentinelpay.payment_operations
WHERE "MerchantId" = :merchant_id
  AND "IdempotencyKey" = :idempotency_key;
```

3. If the provider supports an operation lookup, inspect it using the stored provider reference or native idempotency key.
4. Never change a `Started` operation to `Succeeded` based only on an HTTP timeout. Reconcile against authoritative provider state.
5. Escalate operations older than the provider-specific ambiguity window.

The local `make recovery` drill demonstrates safe resume behavior.

## Incident: reconciliation corrections increase

1. Group corrections by provider and event type in logs/metrics.
2. Inspect webhook signature failures, provider delivery dashboards, and inbox receipt gaps.
3. Check whether provider callbacks arrived after the configured stale threshold.
4. Confirm corrected captures produced exactly one `capture:{paymentId}` ledger journal.
5. If provider `Unknown` results grow, stop automatic assumptions and contact the provider; Unknown is intentionally a no-op.

Reconciliation processes each payment independently, so one failure should not roll back other corrections in the batch.

## Incident: ledger imbalance suspected

The domain prevents newly created unbalanced journals, but operators should still verify database integrity after manual intervention or migration incidents.

```sql
SELECT
    j."Id",
    j."ExternalReference",
    sum(CASE WHEN l."Direction" = 'Debit' THEN l."AmountMinor" ELSE 0 END) AS debits,
    sum(CASE WHEN l."Direction" = 'Credit' THEN l."AmountMinor" ELSE 0 END) AS credits
FROM sentinelpay.ledger_journals j
JOIN sentinelpay.ledger_lines l ON l."JournalId" = j."Id"
GROUP BY j."Id", j."ExternalReference"
HAVING sum(CASE WHEN l."Direction" = 'Debit' THEN l."AmountMinor" ELSE 0 END)
    <> sum(CASE WHEN l."Direction" = 'Credit' THEN l."AmountMinor" ELSE 0 END);
```

Any returned row is severity-one financial integrity loss. Freeze settlement creation for the affected merchant/currency, preserve evidence, and correct with an explicit compensating journal after review. Never edit historical ledger amounts in place.

## Incident: merchant credential compromise

1. Revoke the credential by setting `RevokedAt`.
2. Issue a replacement through the production credential workflow; never store or log the plaintext after issuance.
3. Review payment operations, source IP telemetry, idempotency conflicts, and settlement activity for the merchant.
4. Suspend the merchant if the blast radius is unclear.
5. Rotate related webhook or provider credentials if trust boundaries overlap.

The repository seeds one development key but intentionally does not implement a public key-management endpoint.

## Migration procedure

1. Review generated SQL and lock behavior against production data volume.
2. Back up PostgreSQL and verify restore capability.
3. Apply migrations as a separate deployment step with a dedicated principal.
4. Deploy application instances only after compatible schema exists.
5. Verify readiness, a sandbox authorization, outbox publication, and ledger journal creation.

The demo runs migrations at startup for convenience. Set `Database:InitializeOnStartup=false` in production.

## Backup and restore checks

- Back up PostgreSQL with point-in-time recovery; payment, operation, inbox, ledger, settlement, and outbox tables form one recovery boundary.
- Treat RabbitMQ as delivery infrastructure, not the source of financial truth.
- Redis leases are disposable coordination state and do not require financial recovery.
- Periodically restore a backup into an isolated environment and run ledger-balance and outbox-consistency queries.

## Safe local drills

```bash
make recovery
make chaos
make race
make load
```

Run drills only in an isolated environment. The chaos script intentionally stops and restarts the Compose RabbitMQ service.
