namespace SentinelPay.Domain.Payments;

public sealed class Payment
{
    private readonly List<Capture> _captures = [];
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
    public long VoidedAmountMinor { get; private set; }
    public string? NextActionType { get; private set; }
    public string? NextActionUrl { get; private set; }
    public DateTimeOffset? ActionExpiresAt { get; private set; }
    public DateTimeOffset? AuthorizationExpiresAt { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? AuthorizedAt { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }
    public DateTimeOffset? AuthorizationClosedAt { get; private set; }
    public long Version { get; private set; }
    public long RemainingAuthorizedAmountMinor => Status is PaymentStatus.Authorized or PaymentStatus.PartiallyCaptured
        ? AmountMinor - CapturedAmountMinor - VoidedAmountMinor
        : 0;
    public IReadOnlyCollection<Capture> Captures => _captures.AsReadOnly();
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

        if (_operations.Any(operation => operation.Type == type && operation.IdempotencyKey == normalizedKey))
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

    public void MarkAuthenticationRequired(
        string providerReference,
        string actionType,
        string actionUrl,
        DateTimeOffset actionExpiresAt,
        DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Pending);
        if (actionExpiresAt <= now)
        {
            throw new DomainException("Authentication action expiry must be in the future.");
        }

        ProviderReference = RequireProviderReference(providerReference);
        NextActionType = RequireBounded(actionType, 40, "Next action type");
        NextActionUrl = RequireAbsoluteHttpsUrl(actionUrl);
        ActionExpiresAt = actionExpiresAt;
        Status = PaymentStatus.RequiresAction;
        UpdatedAt = now;
    }

    public void MarkAuthorized(string providerReference, DateTimeOffset authorizationExpiresAt, DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Pending or PaymentStatus.RequiresAction))
        {
            throw new DomainException($"A payment in '{Status}' state cannot be authorized.");
        }

        if (authorizationExpiresAt <= now)
        {
            throw new DomainException("Authorization expiry must be in the future.");
        }

        ProviderReference = RequireProviderReference(providerReference);
        Status = PaymentStatus.Authorized;
        AuthorizedAt = now;
        AuthorizationExpiresAt = authorizationExpiresAt;
        ClearNextAction();
        UpdatedAt = now;
    }

    public void MarkFailed(string code, string message, DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Pending or PaymentStatus.RequiresAction or PaymentStatus.Authorized))
        {
            throw new DomainException($"A payment in '{Status}' state cannot fail.");
        }

        FailureCode = Truncate(string.IsNullOrWhiteSpace(code) ? "provider_error" : code.Trim(), 80);
        FailureMessage = Truncate(message ?? "Provider operation failed.", 500);
        Status = PaymentStatus.Failed;
        ClearNextAction();
        AuthorizationClosedAt = now;
        UpdatedAt = now;
    }

    public Capture RegisterCapture(
        Guid captureId,
        long amountMinor,
        string providerReference,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        EnsureCanCapture(amountMinor, now);
        ValidateOperationIdentity(idempotencyKey, requestHash, "Capture");

        var capture = new Capture(
            captureId,
            Id,
            amountMinor,
            RequireProviderReference(providerReference),
            idempotencyKey.Trim(),
            requestHash,
            now);
        _captures.Add(capture);
        CapturedAmountMinor += amountMinor;
        Status = RemainingAuthorizedAmountMinor == 0 ? PaymentStatus.Captured : PaymentStatus.PartiallyCaptured;
        if (Status == PaymentStatus.Captured)
        {
            CapturedAt = now;
            AuthorizationClosedAt = now;
        }

        UpdatedAt = now;
        return capture;
    }

    public void EnsureCanCapture(long amountMinor, DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Authorized or PaymentStatus.PartiallyCaptured))
        {
            throw new DomainException($"A payment in '{Status}' state cannot be captured.");
        }

        if (AuthorizationExpiresAt is not null && AuthorizationExpiresAt <= now)
        {
            throw new DomainException("The authorization has expired and cannot be captured.");
        }

        var remaining = RemainingAuthorizedAmountMinor;
        if (amountMinor <= 0 || amountMinor > remaining)
        {
            throw new DomainException($"Capture amount must be between 1 and {remaining} minor units.");
        }
    }

    public void VoidRemainingAuthorization(DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Authorized or PaymentStatus.PartiallyCaptured))
        {
            throw new DomainException($"A payment in '{Status}' state cannot be voided.");
        }

        var remaining = RemainingAuthorizedAmountMinor;
        if (remaining <= 0)
        {
            throw new DomainException("The payment has no remaining authorization to void.");
        }

        VoidedAmountMinor += remaining;
        Status = CapturedAmountMinor == 0 ? PaymentStatus.Voided : PaymentStatus.PartiallyCapturedAndVoided;
        AuthorizationClosedAt = now;
        UpdatedAt = now;
    }

    public void Expire(DateTimeOffset now)
    {
        var expiry = Status == PaymentStatus.RequiresAction ? ActionExpiresAt : AuthorizationExpiresAt;
        if (Status is not (PaymentStatus.RequiresAction or PaymentStatus.Authorized or PaymentStatus.PartiallyCaptured) ||
            expiry is null ||
            expiry > now)
        {
            throw new DomainException($"A payment in '{Status}' state is not eligible for expiry.");
        }

        if (Status != PaymentStatus.RequiresAction)
        {
            VoidedAmountMinor += RemainingAuthorizedAmountMinor;
        }

        Status = CapturedAmountMinor == 0 ? PaymentStatus.Expired : PaymentStatus.PartiallyCapturedAndVoided;
        ClearNextAction();
        AuthorizationClosedAt = now;
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
        ValidateOperationIdentity(idempotencyKey, requestHash, "Refund");

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

    public void EnsureCanRefund(long amountMinor)
    {
        if (Status is not (PaymentStatus.Captured or PaymentStatus.PartiallyCapturedAndVoided or PaymentStatus.PartiallyRefunded))
        {
            throw new DomainException(
                $"A payment in '{Status}' state cannot be refunded. Close any remaining authorization first.");
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

    private void ClearNextAction()
    {
        NextActionType = null;
        NextActionUrl = null;
        ActionExpiresAt = null;
    }

    private static string RequireProviderReference(string providerReference) =>
        RequireBounded(providerReference, 120, "Provider reference");

    private static string RequireBounded(string value, int maxLength, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength)
        {
            throw new DomainException($"{fieldName} is required and cannot exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static string RequireAbsoluteHttpsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainException("Next action URL must be an absolute HTTPS URL.");
        }

        var normalized = uri.ToString();
        if (normalized.Length > 1000)
        {
            throw new DomainException("Next action URL cannot exceed 1000 characters.");
        }

        return normalized;
    }

    private static void ValidateOperationIdentity(string idempotencyKey, string requestHash, string operationName)
    {
        var normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        if (normalizedKey.Length is < 8 or > 128)
        {
            throw new DomainException($"{operationName} idempotency key length must be between 8 and 128 characters.");
        }

        EnsureRequestHash(requestHash);
    }

    private static void EnsureRequestHash(string requestHash)
    {
        if (requestHash is null || requestHash.Length != 64 || requestHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new DomainException("Request hash must be a 64-character hexadecimal SHA-256 digest.");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
