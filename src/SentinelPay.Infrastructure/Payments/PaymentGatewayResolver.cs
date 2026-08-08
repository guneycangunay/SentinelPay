using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Payments;

namespace SentinelPay.Infrastructure.Payments;

public sealed class PaymentGatewayResolver : IPaymentGatewayResolver
{
    private readonly IReadOnlyDictionary<string, IPaymentGateway> _gateways;

    public PaymentGatewayResolver(IEnumerable<IPaymentGateway> gateways)
    {
        _gateways = gateways.ToDictionary(gateway => gateway.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IPaymentGateway Resolve(string provider)
    {
        if (_gateways.TryGetValue(provider.Trim(), out var gateway))
        {
            return gateway;
        }

        throw new UnsupportedProviderException(provider, GetProviderNames());
    }

    public IReadOnlyCollection<string> GetProviderNames() =>
        _gateways.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
