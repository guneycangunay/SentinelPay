using SentinelPay.Domain.Payments;

namespace SentinelPay.Application.Payments;

public sealed record CreatePaymentCommand(
    Guid MerchantId,
    string MerchantReference,
    long AmountMinor,
    string Currency,
    string Provider,
    string PaymentMethodToken,
    string IdempotencyKey);

public sealed record ConfirmAuthenticationCommand(
    Guid MerchantId,
    Guid PaymentId,
    string AuthenticationResultToken,
    string IdempotencyKey);

public sealed record CapturePaymentCommand(
    Guid MerchantId,
    Guid PaymentId,
    long AmountMinor,
    string IdempotencyKey);

public sealed record VoidPaymentCommand(Guid MerchantId, Guid PaymentId, string IdempotencyKey);

public sealed record RefundPaymentCommand(Guid MerchantId, Guid PaymentId, long AmountMinor, string IdempotencyKey);

public sealed record PaymentResult(PaymentResponse Payment, bool IsReplay);

public sealed record PaymentNextActionResponse(
    string Type,
    string Url,
    DateTimeOffset ExpiresAt);

public sealed record CaptureResponse(
    Guid Id,
    long AmountMinor,
    string ProviderReference,
    DateTimeOffset CreatedAt);

public sealed record RefundResponse(
    Guid Id,
    long AmountMinor,
    string ProviderReference,
    DateTimeOffset CreatedAt);

public sealed record PaymentOperationResponse(
    Guid Id,
    PaymentOperationType Type,
    PaymentOperationStatus Status,
    string? ProviderReference,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record PaymentResponse(
    Guid Id,
    string MerchantReference,
    long AmountMinor,
    string Currency,
    string Provider,
    string? ProviderReference,
    PaymentStatus Status,
    long CapturedAmountMinor,
    long RemainingAuthorizedAmountMinor,
    long RefundedAmountMinor,
    long VoidedAmountMinor,
    DateTimeOffset? AuthorizationExpiresAt,
    DateTimeOffset? AuthorizationClosedAt,
    PaymentNextActionResponse? NextAction,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<CaptureResponse> Captures,
    IReadOnlyCollection<RefundResponse> Refunds,
    IReadOnlyCollection<PaymentOperationResponse> Operations)
{
    public static PaymentResponse From(Payment payment) => new(
        payment.Id,
        payment.MerchantReference,
        payment.AmountMinor,
        payment.Currency,
        payment.Provider,
        payment.ProviderReference,
        payment.Status,
        payment.CapturedAmountMinor,
        payment.RemainingAuthorizedAmountMinor,
        payment.RefundedAmountMinor,
        payment.VoidedAmountMinor,
        payment.AuthorizationExpiresAt,
        payment.AuthorizationClosedAt,
        payment.NextActionType is not null && payment.NextActionUrl is not null && payment.ActionExpiresAt is not null
            ? new PaymentNextActionResponse(
                payment.NextActionType,
                payment.NextActionUrl,
                payment.ActionExpiresAt.Value)
            : null,
        payment.FailureCode,
        payment.FailureMessage,
        payment.CreatedAt,
        payment.UpdatedAt,
        payment.Captures
            .OrderBy(capture => capture.CreatedAt)
            .Select(capture => new CaptureResponse(
                capture.Id,
                capture.AmountMinor,
                capture.ProviderReference,
                capture.CreatedAt))
            .ToArray(),
        payment.Refunds
            .OrderBy(refund => refund.CreatedAt)
            .Select(refund => new RefundResponse(
                refund.Id,
                refund.AmountMinor,
                refund.ProviderReference,
                refund.CreatedAt))
            .ToArray(),
        payment.Operations
            .OrderBy(operation => operation.StartedAt)
            .Select(operation => new PaymentOperationResponse(
                operation.Id,
                operation.Type,
                operation.Status,
                operation.ProviderReference,
                operation.ErrorCode,
                operation.StartedAt,
                operation.CompletedAt))
            .ToArray());
}
