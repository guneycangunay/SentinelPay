namespace SentinelPay.Domain.Payments;

public sealed class Payment
{
    private readonly List<Refund> _refunds = [];

    private Payment()
    {
    }

    private Payment(
        Guid id,
        string merchantReference,
        long amountMinor,
        string currency,
        string provider,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        MerchantReference = merchantReference;
        AmountMinor = amountMinor;
        Currency = currency;
        Provider = provider;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        Status = PaymentStatus.Pending;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string MerchantReference { get; private set; } = string.Empty;
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;
    public string? ProviderReference { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public long CapturedAmountMinor { get; private set; }
    public long RefundedAmountMinor { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? AuthorizedAt { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }
    public uint Version { get; private set; }
    public IReadOnlyCollection<Refund> Refunds => _refunds.AsReadOnly();

    public static Payment Create(
        string merchantReference,
        long amountMinor,
        string currency,
        string provider,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(merchantReference))
        {
            throw new DomainException("Merchant reference is required.");
        }

        if (amountMinor <= 0)
        {
            throw new DomainException("Payment amount must be greater than zero.");
        }

        if (currency.Length != 3 || currency.Any(character => !char.IsLetter(character)))
        {
            throw new DomainException("Currency must be a three-letter ISO 4217 code.");
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new DomainException("Payment provider is required.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("Idempotency key is required.");
        }

        return new Payment(
            Guid.NewGuid(),
            merchantReference.Trim(),
            amountMinor,
            currency.ToUpperInvariant(),
            provider.Trim().ToLowerInvariant(),
            idempotencyKey.Trim(),
            requestHash,
            now);
    }

    public void MarkAuthorized(string providerReference, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Pending);
        ProviderReference = RequireProviderReference(providerReference);
        Status = PaymentStatus.Authorized;
        AuthorizedAt = now;
        UpdatedAt = now;
    }

    public void MarkFailed(string code, string message, DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Pending or PaymentStatus.Authorized))
        {
            throw new DomainException($"A payment in '{Status}' state cannot fail.");
        }

        FailureCode = string.IsNullOrWhiteSpace(code) ? "provider_error" : code;
        FailureMessage = message;
        Status = PaymentStatus.Failed;
        UpdatedAt = now;
    }

    public void Capture(long amountMinor, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Authorized);

        if (amountMinor != AmountMinor)
        {
            throw new DomainException("SentinelPay currently supports full capture only.");
        }

        CapturedAmountMinor = amountMinor;
        Status = PaymentStatus.Captured;
        CapturedAt = now;
        UpdatedAt = now;
    }

    public Refund RegisterRefund(
        Guid refundId,
        long amountMinor,
        string providerReference,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Captured or PaymentStatus.PartiallyRefunded))
        {
            throw new DomainException($"A payment in '{Status}' state cannot be refunded.");
        }

        var remaining = CapturedAmountMinor - RefundedAmountMinor;
        if (amountMinor <= 0 || amountMinor > remaining)
        {
            throw new DomainException($"Refund amount must be between 1 and {remaining} minor units.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("Refund idempotency key is required.");
        }

        var refund = new Refund(
            refundId,
            Id,
            amountMinor,
            RequireProviderReference(providerReference),
            idempotencyKey.Trim(),
            requestHash,
            now);
        _refunds.Add(refund);
        RefundedAmountMinor += amountMinor;
        Status = RefundedAmountMinor == CapturedAmountMinor
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
        UpdatedAt = now;
        return refund;
    }

    private void EnsureStatus(PaymentStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"Expected payment state '{expected}', but current state is '{Status}'.");
        }
    }

    private static string RequireProviderReference(string providerReference)
    {
        if (string.IsNullOrWhiteSpace(providerReference))
        {
            throw new DomainException("Provider reference is required.");
        }

        return providerReference.Trim();
    }
}
