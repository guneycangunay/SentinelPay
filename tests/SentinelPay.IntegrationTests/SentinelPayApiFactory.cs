using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SentinelPay.IntegrationTests;

public sealed class SentinelPayApiFactory : WebApplicationFactory<Program>
{
    public const string DevelopmentApiKey = "${SENTINELPAY_API_KEY}";
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
                ["Reconciliation:Enabled"] = "false",
                ["Database:InitializeOnStartup"] = "true",
                ["DevelopmentMerchant:Seed"] = "true",
                ["DevelopmentMerchant:Id"] = "2dc5f437-0a11-4c67-a810-b3e784470f73",
                ["DevelopmentMerchant:Name"] = "Acme Commerce Tests",
                ["DevelopmentMerchant:ApiKey"] = DevelopmentApiKey,
                ["Webhooks:mock-bank:Secret"] = "${SENTINELPAY_TEST_SIGNING_MATERIAL}",
                ["Webhooks:sandbox-wallet:Secret"] = "${SENTINELPAY_TEST_SIGNING_MATERIAL}"
            });
        });
    }
}
