using System.Collections.Concurrent;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class SandboxGatewayStateStore : ISandboxGatewayControl
{
    private readonly ConcurrentDictionary<string, GatewayPaymentStatusResult> _states =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failOnceKeys =
        new(StringComparer.Ordinal);

    public void SetState(
        string providerReference,
        GatewayPaymentState state,
        string? errorCode = null,
        string? errorMessage = null)
    {
        _states[providerReference] = new GatewayPaymentStatusResult(state, errorCode, errorMessage);
    }

    public GatewayPaymentStatusResult GetState(string providerReference) =>
        _states.TryGetValue(providerReference, out var state)
            ? state
            : new GatewayPaymentStatusResult(GatewayPaymentState.Unknown, null, null);

    public bool ShouldFailOnce(string idempotencyKey) =>
        _failOnceKeys.TryAdd(idempotencyKey, 0);
}
