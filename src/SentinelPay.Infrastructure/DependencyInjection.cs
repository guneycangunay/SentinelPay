using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Payments;
using SentinelPay.Infrastructure.Locking;
using SentinelPay.Infrastructure.Outbox;
using SentinelPay.Infrastructure.Payments;
using SentinelPay.Infrastructure.Persistence;
using StackExchange.Redis;

namespace SentinelPay.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSentinelPayInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        services.AddDbContext<SentinelPayDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null)));

        services.AddScoped<IPaymentStore, PaymentStore>();
        services.AddScoped<IWebhookInbox, WebhookInbox>();
        services.AddScoped<IOutboxWriter, EfOutboxWriter>();
        services.AddScoped<PaymentService>();
        services.AddScoped<WebhookService>();
        services.AddSingleton<IWebhookSignatureVerifier, HmacWebhookSignatureVerifier>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<IPaymentGateway, MockBankGateway>();
        services.AddSingleton<IPaymentGateway, SandboxWalletGateway>();
        services.AddSingleton<IPaymentGatewayResolver, PaymentGatewayResolver>();

        if (configuration.GetValue("Redis:Enabled", true))
        {
            var redisConnection = configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("ConnectionStrings:Redis is required when Redis is enabled.");
            var redisOptions = ConfigurationOptions.Parse(redisConnection);
            redisOptions.AbortOnConnectFail = false;
            redisOptions.ConnectRetry = 3;
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
            services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        }
        else
        {
            services.AddSingleton<IDistributedLock, InProcessDistributedLock>();
        }

        services.AddScoped<IEventPublisher, LoggingEventPublisher>();
        if (configuration.GetValue("Outbox:DispatcherEnabled", true))
        {
            services.AddHostedService<OutboxDispatcher>();
        }

        if (configuration.GetValue("Reconciliation:Enabled", true))
        {
            services.AddHostedService<ReconciliationWorker>();
        }

        return services;
    }
}
