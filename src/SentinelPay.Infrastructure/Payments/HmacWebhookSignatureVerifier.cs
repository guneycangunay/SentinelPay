using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class HmacWebhookSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;

    public HmacWebhookSignatureVerifier(IConfiguration configuration, IClock clock)
    {
        _configuration = configuration;
        _clock = clock;
    }

    public bool IsValid(string provider, string payload, string signature)
    {
        var secret = _configuration[$"Webhooks:{provider}:Secret"];
        if (string.IsNullOrWhiteSpace(secret) ||
            !TryParseSignature(signature, out var timestamp, out var suppliedSignature))
        {
            return false;
        }

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var tolerance = TimeSpan.FromSeconds(
            _configuration.GetValue("Webhooks:SignatureToleranceSeconds", 300));
        if ((_clock.UtcNow - signedAt).Duration() > tolerance)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));

        try
        {
            var supplied = Convert.FromHexString(suppliedSignature);
            return supplied.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseSignature(
        string header,
        out long timestamp,
        out string signature)
    {
        timestamp = 0;
        signature = string.Empty;
        foreach (var segment in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0].Equals("t", StringComparison.Ordinal) &&
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
            {
                return false;
            }

            if (parts[0].Equals("v1", StringComparison.Ordinal))
            {
                signature = parts[1];
            }
        }

        return timestamp > 0 && !string.IsNullOrWhiteSpace(signature);
    }
}
