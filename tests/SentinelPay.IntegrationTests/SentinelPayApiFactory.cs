using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SentinelPay.IntegrationTests;

public sealed class SentinelPayApiFactory : WebApplicationFactory<Program>
{
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
                ["Database:InitializeOnStartup"] = "true"
            });
        });
    }
}
