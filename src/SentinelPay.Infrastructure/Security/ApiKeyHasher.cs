using System.Security.Cryptography;
using System.Text;

namespace SentinelPay.Infrastructure.Security;

public static class ApiKeyHasher
{
    public static string Hash(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
