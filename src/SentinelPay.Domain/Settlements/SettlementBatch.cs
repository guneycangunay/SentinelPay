namespace SentinelPay.Domain.Settlements;

public enum SettlementStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2
}

public sealed class SettlementBatch
{
    private SettlementBatch()
    {
    }

    private SettlementBatch(
        Guid id,
        Guid merchantId,
        string currency,
        long amountMinor,
        string idempotencyKey,
        DateTimeOffset periodEnd,
        DateTimeOffset createdAt)
    {
        Id = id;
        MerchantId = merchantId;
        Currency = currency;
        AmountMinor = amountMinor;
        IdempotencyKey = idempotencyKey;
        PeriodEnd = periodEnd;
        Status = SettlementStatus.Pending;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public long AmountMinor { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset PeriodEnd { get; private set; }
    public SettlementStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }

    public static SettlementBatch Create(
        Guid merchantId,
        string currency,
        long amountMinor,
        string idempotencyKey,
        DateTimeOffset periodEnd,
        DateTimeOffset now)
    {
        if (merchantId == Guid.Empty || amountMinor <= 0 || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("Settlement requires a merchant and a positive balance.");
        }

        return new SettlementBatch(
            Guid.NewGuid(),
            merchantId,
            currency.ToUpperInvariant(),
            amountMinor,
            idempotencyKey.Trim(),
            periodEnd,
            now);
    }

    public void MarkPaid(DateTimeOffset now)
    {
        if (Status != SettlementStatus.Pending)
        {
            throw new DomainException("Only pending settlements can be paid.");
        }

        Status = SettlementStatus.Paid;
        PaidAt = now;
    }
}
