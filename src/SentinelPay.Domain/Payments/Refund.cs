namespace SentinelPay.Domain.Payments;

public sealed class Refund
{
    private Refund()
    {
    }

    internal Refund(
        Guid id,
        Guid paymentId,
        long amountMinor,
        string providerReference,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        PaymentId = paymentId;
        AmountMinor = amountMinor;
        ProviderReference = providerReference;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public long AmountMinor { get; private set; }
    public string ProviderReference { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
