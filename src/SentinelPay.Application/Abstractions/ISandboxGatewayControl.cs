namespace SentinelPay.Application.Abstractions;

public interface ISandboxGatewayControl
{
    void SetState(
        string providerReference,
        GatewayPaymentState state,
        long? capturedAmountMinor = null,
        string? errorCode = null,
        string? errorMessage = null);
}
