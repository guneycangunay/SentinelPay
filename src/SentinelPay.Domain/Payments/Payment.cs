namespace SentinelPay.Domain.Payments;

public sealed class Payment
{
    private readonly List<Refund> _refunds = [];
    private readonly List<PaymentOperation> _operations = [];

    private Payment()
    {
    }

    private Payment(
        Guid id,
        Guid merchantId,
        string merchantReference,
        long amountMinor,
        string currency,
        string provider,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        MerchantId = merchantId;
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
    public Guid MerchantId { get; private set; }
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
    public IReadOnlyCollection<PaymentOperation> Operations => _operations.AsReadOnly();

    public static Payment Create(
        Guid merchantId,
        string merchantReference,
        long amountMinor,
        string currency,
        string provider,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        var normalizedReference = merchantReference?.Trim() ?? string.Empty;
        var normalizedCurrency = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        var normalizedProvider = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedKey = idempotencyKey?.Trim() ?? string.Empty;

        if (normalizedReference.Length is 0 or > 100)
        {
            throw new DomainException("Merchant reference is required and cannot exceed 100 characters.");
        }

        if (amountMinor <= 0)
        {
            throw new DomainException("Payment amount must be greater than zero.");
        }

        if (normalizedCurrency.Length != 3 || normalizedCurrency.Any(character => !char.IsLetter(character)))
        {
            throw new DomainException("Currency must be a three-letter ISO 4217 code.");
        }

        if (normalizedProvider.Length is 0 or > 40)
        {
            throw new DomainException("Payment provider is required and cannot exceed 40 characters.");
        }

        if (normalizedKey.Length is < 8 or > 128)
        {
            throw new DomainException("Idempotency key length must be between 8 and 128 characters.");
        }

        EnsureRequestHash(requestHash);

        if (merchantId == Guid.Empty)
        {
            throw new DomainException("Merchant id is required.");
        }

        return new Payment(
            Guid.NewGuid(),
            merchantId,
            normalizedReference,
            amountMinor,
            normalizedCurrency,
            normalizedProvider,
            normalizedKey,
            requestHash,
            now);
    }

    public PaymentOperation StartOperation(
        PaymentOperationType type,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        var normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        if (normalizedKey.Length is < 8 or > 128)
        {
            throw new DomainException("Operation idempotency key length must be between 8 and 128 characters.");
        }

        EnsureRequestHash(requestHash);

        if (_operations.Any(operation =>
                operation.Type == type && operation.IdempotencyKey == normalizedKey))
        {
            throw new DomainException("The payment operation already exists.");
        }

        var operation = new PaymentOperation(
            Guid.NewGuid(),
            MerchantId,
            Id,
            type,
            normalizedKey,
            requestHash,
            now);
        _operations.Add(operation);
        return operation;
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

        var normalizedCode = string.IsNullOrWhiteSpace(code) ? "provider_error" : code.Trim();
        FailureCode = normalizedCode[..Math.Min(normalizedCode.Length, 80)];
        FailureMessage = message[..Math.Min(message.Length, 500)];
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
        EnsureCanRefund(amountMinor);

        var normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        if (normalizedKey.Length is < 8 or > 128)
        {
            throw new DomainException("Refund idempotency key length must be between 8 and 128 characters.");
        }

        EnsureRequestHash(requestHash);

        var refund = new Refund(
            refundId,
            Id,
            amountMinor,
            RequireProviderReference(providerReference),
            normalizedKey,
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

    public void EnsureCanRefund(long amountMinor)
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
        var normalized = providerReference?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 120)
        {
            throw new DomainException("Provider reference is required and cannot exceed 120 characters.");
        }

        return normalized;
    }

    private static void EnsureRequestHash(string requestHash)
    {
        if (requestHash is null || requestHash.Length != 64 || requestHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new DomainException("Request hash must be a 64-character hexadecimal SHA-256 digest.");
        }
    }
}
