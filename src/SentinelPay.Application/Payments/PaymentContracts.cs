using SentinelPay.Domain.Payments;

namespace SentinelPay.Application.Payments;

public sealed record CreatePaymentCommand(
    string MerchantReference,
    long AmountMinor,
    string Currency,
    string Provider,
    string PaymentMethodToken,
    string IdempotencyKey);

public sealed record CapturePaymentCommand(Guid PaymentId, string IdempotencyKey);

public sealed record RefundPaymentCommand(Guid PaymentId, long AmountMinor, string IdempotencyKey);

public sealed record PaymentResult(PaymentResponse Payment, bool IsReplay);

public sealed record RefundResponse(
    Guid Id,
    long AmountMinor,
    string ProviderReference,
    DateTimeOffset CreatedAt);

public sealed record PaymentResponse(
    Guid Id,
    string MerchantReference,
    long AmountMinor,
    string Currency,
    string Provider,
    string? ProviderReference,
    PaymentStatus Status,
    long CapturedAmountMinor,
    long RefundedAmountMinor,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<RefundResponse> Refunds)
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
        payment.RefundedAmountMinor,
        payment.FailureCode,
        payment.FailureMessage,
        payment.CreatedAt,
        payment.UpdatedAt,
        payment.Refunds
            .OrderBy(refund => refund.CreatedAt)
            .Select(refund => new RefundResponse(
                refund.Id,
                refund.AmountMinor,
                refund.ProviderReference,
                refund.CreatedAt))
            .ToArray());
}
