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
    private readonly IClock _clock;

    public ReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IClock clock,
        ILogger<ReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _clock = clock;
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
        var staleBefore = _clock.UtcNow.AddMinutes(
            -_configuration.GetValue("Reconciliation:StaleAfterMinutes", 2));
        Guid[] paymentIds;
        await using (var discoveryScope = _scopeFactory.CreateAsyncScope())
        {
            var discoveryDb = discoveryScope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
            paymentIds = await discoveryDb.Payments
                .AsNoTracking()
                .Where(payment => payment.Status == PaymentStatus.Authorized && payment.UpdatedAt <= staleBefore)
                .OrderBy(payment => payment.UpdatedAt)
                .Select(payment => payment.Id)
                .Take(_configuration.GetValue("Reconciliation:BatchSize", 50))
                .ToArrayAsync(cancellationToken);
        }

        var corrected = 0;
        foreach (var paymentId in paymentIds)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
                var resolver = scope.ServiceProvider.GetRequiredService<IPaymentGatewayResolver>();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
                var ledger = scope.ServiceProvider.GetRequiredService<ILedgerWriter>();
                var payment = await dbContext.Payments
                    .Include(item => item.Operations)
                    .SingleOrDefaultAsync(
                        item => item.Id == paymentId && item.Status == PaymentStatus.Authorized,
                        cancellationToken);
                if (payment is null)
                {
                    continue;
                }

                var gateway = resolver.Resolve(payment.Provider);
                var external = await gateway.GetStatusAsync(
                    payment.ProviderReference ?? string.Empty,
                    cancellationToken);
                var now = _clock.UtcNow;

                switch (external.State)
                {
                    case GatewayPaymentState.Captured:
                    {
                        var captureOperation = payment.StartOperation(
                            PaymentOperationType.Reconcile,
                            $"reconcile:{payment.Id:N}:captured",
                            HashExternalState(GatewayPaymentState.Captured, null),
                            now);
                        payment.Capture(payment.AmountMinor, now);
                        captureOperation.Succeed(payment.ProviderReference, now);
                        await ledger.RecordCaptureAsync(payment, now, cancellationToken);
                        outbox.Add(
                            "payment.reconciled-captured.v2",
                            payment.Id,
                            new { payment.Id, payment.MerchantId, payment.ProviderReference },
                            now);
                        break;
                    }
                    case GatewayPaymentState.Failed:
                    {
                        var failureOperation = payment.StartOperation(
                            PaymentOperationType.Reconcile,
                            $"reconcile:{payment.Id:N}:failed",
                            HashExternalState(GatewayPaymentState.Failed, external.ErrorCode),
                            now);
                        payment.MarkFailed(
                            external.ErrorCode ?? "reconciled_failure",
                            external.ErrorMessage ?? "The provider reported a failed payment during reconciliation.",
                            now);
                        failureOperation.Succeed(payment.ProviderReference, now);
                        outbox.Add(
                            "payment.reconciled-failed.v2",
                            payment.Id,
                            new { payment.Id, payment.MerchantId, payment.FailureCode },
                            now);
                        break;
                    }
                    case GatewayPaymentState.Authorized:
                    case GatewayPaymentState.Unknown:
                    default:
                        continue;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                corrected++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Reconciliation skipped payment {PaymentId}; the remaining batch will continue.",
                    paymentId);
            }
        }

        if (paymentIds.Length > 0)
        {
            _logger.LogInformation(
                "Inspected {PaymentCount} stale authorizations and corrected {CorrectedCount}.",
                paymentIds.Length,
                corrected);
        }
    }

    private static string HashExternalState(GatewayPaymentState state, string? errorCode) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{state}:{errorCode}")));
}
