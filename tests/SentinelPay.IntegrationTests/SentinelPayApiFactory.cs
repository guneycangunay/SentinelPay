using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgresConnectionString,
                ["Redis:Enabled"] = "false",
                ["Outbox:DispatcherEnabled"] = "false",
                ["Messaging:ConsumerEnabled"] = "false",
                ["Reconciliation:Enabled"] = "false",
                ["PaymentExpiry:Enabled"] = "false",
                ["Database:InitializeOnStartup"] = "true",
                ["DevelopmentMerchant:Seed"] = "true",
                ["DevelopmentMerchant:Id"] = "2dc5f437-0a11-4c67-a810-b3e784470f73",
                ["DevelopmentMerchant:Name"] = "Acme Commerce Tests",
                ["DevelopmentMerchant:ApiKey"] = DevelopmentApiKey,
                ["Webhooks:mock-bank:Secret"] = WebhookSigningMaterial,
                ["Webhooks:sandbox-wallet:Secret"] = WebhookSigningMaterial
            });
        });
    }

    private static string DeriveFixtureValue(string purpose) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{typeof(SentinelPayApiFactory).FullName}:{purpose}")));
}
