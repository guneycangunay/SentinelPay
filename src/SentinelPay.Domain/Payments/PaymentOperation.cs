namespace SentinelPay.Domain.Payments;

public enum PaymentOperationType
{
    Authorize = 0,
    Capture = 1,
    Refund = 2,
    Reconcile = 3
}

public enum PaymentOperationStatus
{
    Started = 0,
    Succeeded = 1,
    Failed = 2
}

public sealed class PaymentOperation
{
    private PaymentOperation()
    {
    }

    internal PaymentOperation(
        Guid id,
        Guid merchantId,
        Guid paymentId,
        PaymentOperationType type,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        Id = id;
        MerchantId = merchantId;
        PaymentId = paymentId;
        Type = type;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        Status = PaymentOperationStatus.Started;
        StartedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid PaymentId { get; private set; }
    public PaymentOperationType Type { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public PaymentOperationStatus Status { get; private set; }
    public string? ProviderReference { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Succeed(string? providerReference, DateTimeOffset now)
    {
        EnsureStarted();
        ProviderReference = providerReference;
        Status = PaymentOperationStatus.Succeeded;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void Fail(string errorCode, string errorMessage, DateTimeOffset now)
    {
        EnsureStarted();
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Status = PaymentOperationStatus.Failed;
        CompletedAt = now;
        UpdatedAt = now;
    }

    private void EnsureStarted()
    {
        if (Status != PaymentOperationStatus.Started)
        {
            throw new DomainException($"Operation '{Id}' has already completed.");
        }
    }
}
