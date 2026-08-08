using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class SandboxWalletGateway : IPaymentGateway
{
    private readonly SandboxGatewayStateStore _stateStore;

    public SandboxWalletGateway(SandboxGatewayStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public string Name => "sandbox-wallet";

    public async Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(35), cancellationToken);

        if (request.PaymentMethodToken.Equals("wallet_locked", StringComparison.OrdinalIgnoreCase))
        {
            return new GatewayAuthorizationResult(
                GatewayAuthorizationState.Declined,
                null,
                null,
                "wallet_locked",
                "The wallet is locked.");
        }

        var providerReference = DeterministicReference.Create(
            "sw_auth",
            request.PaymentId.ToString("N"),
            request.IdempotencyKey);
        _stateStore.SetAuthorized(providerReference, request.AmountMinor);
        return new GatewayAuthorizationResult(
            GatewayAuthorizationState.Authorized,
            providerReference,
            null,
            null,
            null);
    }

    public Task<GatewayAuthorizationResult> CompleteAuthenticationAsync(
        GatewayAuthenticationRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new GatewayAuthorizationResult(
            GatewayAuthorizationState.Declined,
            null,
            null,
            "authentication_not_supported",
            "The sandbox wallet does not use cardholder authentication."));

    public async Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        _stateStore.AddCapture(request.ProviderReference, request.AmountMinor);
        return new GatewayOperationResult(
            true,
            DeterministicReference.Create("sw_cap", request.CaptureId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public async Task<GatewayOperationResult> VoidAsync(
        GatewayVoidRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        _stateStore.CloseAuthorization(request.ProviderReference);
        return new GatewayOperationResult(
            true,
            DeterministicReference.Create("sw_void", request.PaymentId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public async Task<GatewayRefundResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken);
        return new GatewayRefundResult(
            true,
            DeterministicReference.Create("sw_ref", request.RefundId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public Task<GatewayPaymentStatusResult> GetStatusAsync(
        string providerReference,
        CancellationToken cancellationToken) =>
        Task.FromResult(_stateStore.GetState(providerReference));
}
