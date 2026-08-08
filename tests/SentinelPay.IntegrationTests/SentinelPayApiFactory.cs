using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SentinelPay.IntegrationTests;

public sealed class SentinelPayApiFactory : WebApplicationFactory<Program>
{
    public static string DevelopmentApiKey { get; } = DeriveFixtureValue("api-key");
    public static string WebhookSigningMaterial { get; } = DeriveFixtureValue("webhook-signing");
    private readonly string _postgresConnectionString;

    public SentinelPayApiFactory(string postgresConnectionString)
    {
        _postgresConnectionString = postgresConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Postgres", _postgresConnectionString)
            .UseSetting("Redis:Enabled", "false")
            .UseSetting("Outbox:DispatcherEnabled", "false")
            .UseSetting("Messaging:ConsumerEnabled", "false")
            .UseSetting("Reconciliation:Enabled", "false")
            .UseSetting("PaymentExpiry:Enabled", "false")
            .UseSetting("Database:InitializeOnStartup", "true")
            .UseSetting("DevelopmentMerchant:Seed", "true")
            .UseSetting("DevelopmentMerchant:Id", "2dc5f437-0a11-4c67-a810-b3e784470f73")
            .UseSetting("DevelopmentMerchant:Name", "Acme Commerce Tests")
            .UseSetting("DevelopmentMerchant:ApiKey", DevelopmentApiKey)
            .UseSetting("Webhooks:mock-bank:Secret", WebhookSigningMaterial)
            .UseSetting("Webhooks:sandbox-wallet:Secret", WebhookSigningMaterial);
    }

    private static string DeriveFixtureValue(string purpose) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{typeof(SentinelPayApiFactory).FullName}:{purpose}")));
}
