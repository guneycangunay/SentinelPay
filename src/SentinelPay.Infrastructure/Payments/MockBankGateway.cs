using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class MockBankGateway : IPaymentGateway
{
    private readonly SandboxGatewayStateStore _stateStore;

    public MockBankGateway(SandboxGatewayStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public string Name => "mock-bank";

    public async Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);

        if (request.PaymentMethodToken.Equals("tok_transient_once", StringComparison.OrdinalIgnoreCase) &&
            _stateStore.ShouldFailOnce($"{request.PaymentId:N}:{request.IdempotencyKey}"))
        {
            throw new HttpRequestException("Simulated transient provider connection failure.");
        }

        if (request.PaymentMethodToken.Equals("tok_timeout", StringComparison.OrdinalIgnoreCase))
        {
            throw new TimeoutException("Simulated provider timeout after operation persistence.");
        }

        var providerReference = DeterministicReference.Create(
            "mb_auth",
            request.PaymentId.ToString("N"),
            request.IdempotencyKey);
        var result = request.PaymentMethodToken.ToLowerInvariant() switch
        {
            "tok_declined" => Declined("card_declined", "The issuing bank declined the payment."),
            "tok_insufficient_funds" => Declined("insufficient_funds", "The card has insufficient funds."),
            "tok_3ds_challenge" => new GatewayAuthorizationResult(
                GatewayAuthorizationState.RequiresAction,
                providerReference,
                new GatewayNextAction(
                    "redirect",
                    $"https://sandbox.sentinelpay.dev/3ds/{providerReference}",
                    DateTimeOffset.UtcNow.AddMinutes(10)),
                null,
                null),
            _ => Authorized(providerReference)
        };

        if (result.State == GatewayAuthorizationState.Authorized)
        {
            _stateStore.SetAuthorized(providerReference, request.AmountMinor);
        }
        else if (result.State == GatewayAuthorizationState.RequiresAction)
        {
            _stateStore.SetState(providerReference, GatewayPaymentState.RequiresAction, 0);
        }

        return result;
    }

    public async Task<GatewayAuthorizationResult> CompleteAuthenticationAsync(
        GatewayAuthenticationRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(45), cancellationToken);
        if (request.AuthenticationResultToken.Equals("auth_failed", StringComparison.OrdinalIgnoreCase))
        {
            _stateStore.SetState(
                request.ProviderReference,
                GatewayPaymentState.Failed,
                0,
                "authentication_failed",
                "The cardholder did not complete the issuer challenge.");
            return Declined("authentication_failed", "The cardholder did not complete the issuer challenge.");
        }

        _stateStore.SetAuthorized(request.ProviderReference, request.AmountMinor);
        return new GatewayAuthorizationResult(
            GatewayAuthorizationState.Authorized,
            request.ProviderReference,
            null,
            null,
            null);
    }

    public async Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
        _stateStore.AddCapture(request.ProviderReference, request.AmountMinor);
        return new GatewayOperationResult(
            true,
            DeterministicReference.Create("mb_cap", request.CaptureId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public async Task<GatewayOperationResult> VoidAsync(
        GatewayVoidRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(35), cancellationToken);
        _stateStore.CloseAuthorization(request.ProviderReference);
        return new GatewayOperationResult(
            true,
            DeterministicReference.Create("mb_void", request.PaymentId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public async Task<GatewayRefundResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        return new GatewayRefundResult(
            true,
            DeterministicReference.Create("mb_ref", request.RefundId.ToString("N"), request.IdempotencyKey),
            null,
            null);
    }

    public Task<GatewayPaymentStatusResult> GetStatusAsync(
        string providerReference,
        CancellationToken cancellationToken) =>
        Task.FromResult(_stateStore.GetState(providerReference));

    private static GatewayAuthorizationResult Authorized(string providerReference) =>
        new(GatewayAuthorizationState.Authorized, providerReference, null, null, null);

    private static GatewayAuthorizationResult Declined(string code, string message) =>
        new(GatewayAuthorizationState.Declined, null, null, code, message);
}
