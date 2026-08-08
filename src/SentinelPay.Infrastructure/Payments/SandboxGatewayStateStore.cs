using System.Collections.Concurrent;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class SandboxGatewayStateStore : ISandboxGatewayControl
{
    private readonly ConcurrentDictionary<string, GatewayPaymentStatusResult> _states =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _authorizedAmounts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failOnceKeys =
        new(StringComparer.Ordinal);

    public void SetState(
        string providerReference,
        GatewayPaymentState state,
        long? capturedAmountMinor = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        _states[providerReference] = new GatewayPaymentStatusResult(
            state,
            capturedAmountMinor,
            errorCode,
            errorMessage);
    }

    public void AddCapture(string providerReference, long amountMinor)
    {
        _states.AddOrUpdate(
            providerReference,
            _ => new GatewayPaymentStatusResult(
                GatewayPaymentState.PartiallyCaptured,
                amountMinor,
                null,
                null),
            (_, current) =>
            {
                var captured = (current.CapturedAmountMinor ?? 0) + amountMinor;
                var state = _authorizedAmounts.TryGetValue(providerReference, out var authorized) && captured == authorized
                    ? GatewayPaymentState.Captured
                    : GatewayPaymentState.PartiallyCaptured;
                return current with { State = state, CapturedAmountMinor = captured };
            });
    }

    public void SetAuthorized(string providerReference, long amountMinor)
    {
        _authorizedAmounts[providerReference] = amountMinor;
        SetState(providerReference, GatewayPaymentState.Authorized, 0);
    }

    public void CloseAuthorization(string providerReference)
    {
        _states.AddOrUpdate(
            providerReference,
            _ => new GatewayPaymentStatusResult(GatewayPaymentState.Voided, 0, null, null),
            (_, current) => current with
            {
                State = (current.CapturedAmountMinor ?? 0) == 0
                    ? GatewayPaymentState.Voided
                    : GatewayPaymentState.PartiallyCapturedAndVoided
            });
    }

    public GatewayPaymentStatusResult GetState(string providerReference) =>
        _states.TryGetValue(providerReference, out var state)
            ? state
            : new GatewayPaymentStatusResult(GatewayPaymentState.Unknown, null, null, null);

    public bool ShouldFailOnce(string idempotencyKey) =>
        _failOnceKeys.TryAdd(idempotencyKey, 0);
}
