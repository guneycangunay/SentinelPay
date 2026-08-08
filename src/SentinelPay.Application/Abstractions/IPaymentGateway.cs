namespace SentinelPay.Application.Abstractions;

public interface IPaymentGateway
{
    string Name { get; }

    Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request,
        CancellationToken cancellationToken);

    Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
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

public sealed record GatewayCaptureRequest(
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

public sealed record GatewayAuthorizationResult(
    bool IsSuccessful,
    string? ProviderReference,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record GatewayOperationResult(bool IsSuccessful, string? ErrorCode, string? ErrorMessage);

public sealed record GatewayRefundResult(
    bool IsSuccessful,
    string? ProviderReference,
    string? ErrorCode,
    string? ErrorMessage);

public enum GatewayPaymentState
{
    Authorized,
    Captured,
    Failed,
    Unknown
}

public sealed record GatewayPaymentStatusResult(
    GatewayPaymentState State,
    string? ErrorCode,
    string? ErrorMessage);
