using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class HmacWebhookSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly IConfiguration _configuration;

    public HmacWebhookSignatureVerifier(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsValid(string provider, string payload, string signature)
    {
        var secret = _configuration[$"Webhooks:{provider}:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload));

        try
        {
            var supplied = Convert.FromHexString(signature);
            return supplied.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
