using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class MockBankGateway : IPaymentGateway
{
    public string Name => "mock-bank";

    public async Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);

        return request.PaymentMethodToken.ToLowerInvariant() switch
        {
            "tok_declined" => new(false, null, "card_declined", "The issuing bank declined the payment."),
            "tok_insufficient_funds" => new(false, null, "insufficient_funds", "The card has insufficient funds."),
            _ => new(
                true,
                DeterministicReference.Create("mb_auth", request.IdempotencyKey),
                null,
                null)
        };
    }

    public async Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
        return new GatewayOperationResult(true, null, null);
    }

    public async Task<GatewayRefundResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        return new GatewayRefundResult(
            true,
            DeterministicReference.Create("mb_ref", request.PaymentId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public Task<GatewayPaymentStatusResult> GetStatusAsync(
        string providerReference,
        CancellationToken cancellationToken) =>
        Task.FromResult(new GatewayPaymentStatusResult(GatewayPaymentState.Authorized, null, null));
}
