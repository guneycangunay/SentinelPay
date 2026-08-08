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

        var result = request.PaymentMethodToken.ToLowerInvariant() switch
        {
            "tok_declined" => new(false, null, "card_declined", "The issuing bank declined the payment."),
            "tok_insufficient_funds" => new(false, null, "insufficient_funds", "The card has insufficient funds."),
            _ => new(
                true,
                DeterministicReference.Create(
                    "mb_auth",
                    request.PaymentId.ToString("N"),
                    request.IdempotencyKey),
                null,
                null)
        };
        if (result.IsSuccessful && result.ProviderReference is not null)
        {
            _stateStore.SetState(result.ProviderReference, GatewayPaymentState.Authorized);
        }

        return result;
    }

    public async Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
        _stateStore.SetState(request.ProviderReference, GatewayPaymentState.Captured);
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
        Task.FromResult(_stateStore.GetState(providerReference));
}
