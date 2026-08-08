namespace SentinelPay.Application.Abstractions;

public interface IPaymentGatewayResolver
{
    IPaymentGateway Resolve(string provider);
    IReadOnlyCollection<string> GetProviderNames();
}
