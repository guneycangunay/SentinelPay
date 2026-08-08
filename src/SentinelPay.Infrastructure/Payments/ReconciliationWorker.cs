using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Payments;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Payments;

public sealed class ReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReconciliationWorker> _logger;

    public ReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_configuration.GetValue("Reconciliation:IntervalSeconds", 30));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Payment reconciliation cycle failed.");
            }
        }
    }

    private async Task ReconcileBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPaymentGatewayResolver>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(
            -_configuration.GetValue("Reconciliation:StaleAfterMinutes", 2));

        var payments = await dbContext.Payments
            .Where(payment => payment.Status == PaymentStatus.Authorized && payment.UpdatedAt <= staleBefore)
            .OrderBy(payment => payment.UpdatedAt)
            .Take(_configuration.GetValue("Reconciliation:BatchSize", 50))
            .ToListAsync(cancellationToken);

        foreach (var payment in payments)
        {
            var gateway = resolver.Resolve(payment.Provider);
            var external = await gateway.GetStatusAsync(
                payment.ProviderReference ?? string.Empty,
                cancellationToken);

            switch (external.State)
            {
                case GatewayPaymentState.Captured:
                    payment.Capture(payment.AmountMinor, DateTimeOffset.UtcNow);
                    outbox.Add(
                        "payment.reconciled-captured.v1",
                        payment.Id,
                        new { payment.Id, payment.ProviderReference },
                        DateTimeOffset.UtcNow);
                    break;
                case GatewayPaymentState.Failed:
                    payment.MarkFailed(
                        external.ErrorCode ?? "reconciled_failure",
                        external.ErrorMessage ?? "The provider reported a failed payment during reconciliation.",
                        DateTimeOffset.UtcNow);
                    outbox.Add(
                        "payment.reconciled-failed.v1",
                        payment.Id,
                        new { payment.Id, payment.FailureCode },
                        DateTimeOffset.UtcNow);
                    break;
                case GatewayPaymentState.Authorized:
                case GatewayPaymentState.Unknown:
                default:
                    break;
            }
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (payments.Count > 0)
        {
            _logger.LogInformation("Reconciled {PaymentCount} stale authorized payments.", payments.Count);
        }
    }
}
