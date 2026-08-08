namespace SentinelPay.Application.Abstractions;

public interface ISandboxGatewayControl
{
    void SetState(string providerReference, GatewayPaymentState state, string? errorCode = null, string? errorMessage = null);
}
