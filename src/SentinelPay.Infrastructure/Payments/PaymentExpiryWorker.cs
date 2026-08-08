using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Payments;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Payments;

public sealed class PaymentExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly ILogger<PaymentExpiryWorker> _logger;

    public PaymentExpiryWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IClock clock,
        ILogger<PaymentExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_configuration.GetValue("PaymentExpiry:IntervalSeconds", 30));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExpireBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Payment expiry cycle failed.");
            }
        }
    }

    private async Task ExpireBatchAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        ExpiryCandidate[] candidates;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
            candidates = await dbContext.Payments
                .AsNoTracking()
                .Where(payment =>
                    (payment.Status == PaymentStatus.RequiresAction && payment.ActionExpiresAt <= now) ||
                    ((payment.Status == PaymentStatus.Authorized || payment.Status == PaymentStatus.PartiallyCaptured) &&
                     payment.AuthorizationExpiresAt <= now))
                .OrderBy(payment => payment.UpdatedAt)
                .Select(payment => new ExpiryCandidate(payment.Id, payment.MerchantId))
                .Take(_configuration.GetValue("PaymentExpiry:BatchSize", 100))
                .ToArrayAsync(cancellationToken);
        }

        foreach (var candidate in candidates)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
            var distributedLock = scope.ServiceProvider.GetRequiredService<IDistributedLock>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            await using var lease = await distributedLock.AcquireAsync(
                $"payment:mutation:{candidate.MerchantId:N}:{candidate.PaymentId:N}",
                TimeSpan.FromSeconds(30),
                cancellationToken);
            var payment = await dbContext.Payments
                .Include(item => item.Operations)
                .SingleOrDefaultAsync(
                    item => item.Id == candidate.PaymentId && item.MerchantId == candidate.MerchantId,
                    cancellationToken);
            if (payment is null)
            {
                continue;
            }

            var expiry = payment.Status == PaymentStatus.RequiresAction
                ? payment.ActionExpiresAt
                : payment.AuthorizationExpiresAt;
            if (expiry is null || expiry > _clock.UtcNow ||
                payment.Status is not (PaymentStatus.RequiresAction or PaymentStatus.Authorized or PaymentStatus.PartiallyCaptured))
            {
                continue;
            }

            var requestHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{payment.Id:N}:{expiry.Value.ToUnixTimeSeconds()}")));
            var operation = payment.StartOperation(
                PaymentOperationType.Reconcile,
                $"expire:{payment.Id:N}:{expiry.Value.ToUnixTimeSeconds()}",
                requestHash,
                _clock.UtcNow);
            payment.Expire(_clock.UtcNow);
            operation.Succeed(payment.ProviderReference, _clock.UtcNow);
            outbox.Add(
                "payment.authorization-expired.v1",
                payment.Id,
                new { payment.Id, payment.MerchantId, payment.Status, payment.VoidedAmountMinor },
                _clock.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed record ExpiryCandidate(Guid PaymentId, Guid MerchantId);
}
