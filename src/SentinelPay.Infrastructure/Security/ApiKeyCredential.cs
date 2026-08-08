namespace SentinelPay.Infrastructure.Security;

public sealed class ApiKeyCredential
{
    public Guid Id { get; init; }
    public Guid MerchantId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string KeyHash { get; init; } = string.Empty;
    public string Scopes { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
}
