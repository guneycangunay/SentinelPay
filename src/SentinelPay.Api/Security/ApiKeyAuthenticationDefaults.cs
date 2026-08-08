namespace SentinelPay.Api.Security;

public static class ApiKeyAuthenticationDefaults
{
    public const string Scheme = "MerchantApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string MerchantIdClaim = "merchant_id";
}

public static class SentinelPayPolicies
{
    public const string PaymentsRead = "payments:read";
    public const string PaymentsWrite = "payments:write";
    public const string LedgerRead = "ledger:read";
    public const string SettlementsRead = "settlements:read";
    public const string SettlementsWrite = "settlements:write";
    public const string ReconciliationWrite = "reconciliation:write";
}
