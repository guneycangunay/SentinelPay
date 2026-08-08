namespace SentinelPay.Application.Payments;

public sealed class PaymentNotFoundException : Exception
{
    public PaymentNotFoundException(Guid paymentId)
        : base($"Payment '{paymentId}' was not found.")
    {
    }
}

public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException(string message) : base(message)
    {
    }
}

public sealed class UnsupportedProviderException : Exception
{
    public UnsupportedProviderException(string provider, IEnumerable<string> supportedProviders)
        : base($"Provider '{provider}' is not supported. Supported providers: {string.Join(", ", supportedProviders)}.")
    {
    }
}

public sealed class PaymentProviderException : Exception
{
    public PaymentProviderException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class InvalidWebhookSignatureException : Exception
{
    public InvalidWebhookSignatureException() : base("The webhook signature is invalid.")
    {
    }
}

public sealed class InvalidWebhookPayloadException : Exception
{
    public InvalidWebhookPayloadException(string message) : base(message)
    {
    }
}
