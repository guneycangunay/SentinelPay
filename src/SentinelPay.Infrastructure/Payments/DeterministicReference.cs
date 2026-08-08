using System.Security.Cryptography;
using System.Text;

namespace SentinelPay.Infrastructure.Payments;

internal static class DeterministicReference
{
    public static string Create(string prefix, params string[] values)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(':', values)));
        return $"{prefix}_{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }
}
