namespace SentinelPay.Api.Security;

public static class MerchantContextExtensions
{
    public static Guid GetMerchantId(this HttpContext context)
    {
        var value = context.User.FindFirst(ApiKeyAuthenticationDefaults.MerchantIdClaim)?.Value;
        return Guid.TryParse(value, out var merchantId)
            ? merchantId
            : throw new UnauthorizedAccessException("Merchant identity is unavailable.");
    }
}
