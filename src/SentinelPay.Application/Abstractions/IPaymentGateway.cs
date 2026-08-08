namespace SentinelPay.Application.Abstractions;

public interface IPaymentGateway
{
    string Name { get; }

    Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request,
        CancellationToken cancellationToken);

    Task<GatewayAuthorizationResult> CompleteAuthenticationAsync(
        GatewayAuthenticationRequest request,
        CancellationToken cancellationToken);

    Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken);

    Task<GatewayOperationResult> VoidAsync(
        GatewayVoidRequest request,
        CancellationToken cancellationToken);

    Task<GatewayRefundResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken);

    Task<GatewayPaymentStatusResult> GetStatusAsync(
        string providerReference,
        CancellationToken cancellationToken);
}

public sealed record GatewayAuthorizationRequest(
    Guid PaymentId,
    long AmountMinor,
    string Currency,
    string PaymentMethodToken,
    string IdempotencyKey);

public sealed record GatewayAuthenticationRequest(
    Guid PaymentId,
    string ProviderReference,
    long AmountMinor,
    string AuthenticationResultToken,
    string IdempotencyKey);

public sealed record GatewayCaptureRequest(
    Guid PaymentId,
    Guid CaptureId,
    string ProviderReference,
    long AmountMinor,
    string IdempotencyKey);

public sealed record GatewayVoidRequest(
    Guid PaymentId,
    string ProviderReference,
    long AmountMinor,
    string IdempotencyKey);

public sealed record GatewayRefundRequest(
    Guid PaymentId,
    Guid RefundId,
    string ProviderReference,
    long AmountMinor,
    string IdempotencyKey);

public enum GatewayAuthorizationState
{
    Authorized,
    RequiresAction,
    Declined
}

public sealed record GatewayNextAction(
    string Type,
    string Url,
    DateTimeOffset ExpiresAt);

public sealed record GatewayAuthorizationResult(
    GatewayAuthorizationState State,
    string? ProviderReference,
    GatewayNextAction? NextAction,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsSuccessful => State is GatewayAuthorizationState.Authorized or GatewayAuthorizationState.RequiresAction;
}

public sealed record GatewayOperationResult(
    bool IsSuccessful,
    string? ProviderReference,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record GatewayRefundResult(
    bool IsSuccessful,
    string? ProviderReference,
    string? ErrorCode,
    string? ErrorMessage);

public enum GatewayPaymentState
{
    RequiresAction,
    Authorized,
    PartiallyCaptured,
    Captured,
    PartiallyCapturedAndVoided,
    Voided,
    Refunded,
    Failed,
    Unknown
}

public sealed record GatewayPaymentStatusResult(
    GatewayPaymentState State,
    long? CapturedAmountMinor,
    string? ErrorCode,
    string? ErrorMessage);
