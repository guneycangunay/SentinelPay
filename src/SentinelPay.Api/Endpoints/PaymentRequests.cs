namespace SentinelPay.Api.Endpoints;

public sealed record CreatePaymentRequest(
    string MerchantReference,
    long AmountMinor,
    string Currency,
    string Provider,
    string PaymentMethodToken);

public sealed record RefundPaymentRequest(long AmountMinor);
