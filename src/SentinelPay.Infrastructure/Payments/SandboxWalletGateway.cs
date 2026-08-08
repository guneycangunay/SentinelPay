using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class SandboxWalletGateway : IPaymentGateway
{
    public string Name => "sandbox-wallet";

    public async Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(35), cancellationToken);

        if (request.PaymentMethodToken.Equals("wallet_locked", StringComparison.OrdinalIgnoreCase))
        {
            return new GatewayAuthorizationResult(false, null, "wallet_locked", "The wallet is locked.");
        }

        return new GatewayAuthorizationResult(
            true,
            DeterministicReference.Create("sw_auth", request.IdempotencyKey),
            null,
            null);
    }

    public async Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        return new GatewayOperationResult(true, null, null);
    }

    public async Task<GatewayRefundResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken);
        return new GatewayRefundResult(
            true,
            DeterministicReference.Create("sw_ref", request.PaymentId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public Task<GatewayPaymentStatusResult> GetStatusAsync(
        string providerReference,
        CancellationToken cancellationToken) =>
        Task.FromResult(new GatewayPaymentStatusResult(GatewayPaymentState.Authorized, null, null));
}
