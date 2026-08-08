using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Outbox;

public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan EmptyQueueDelay = TimeSpan.FromSeconds(2);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await DispatchBatchAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(EmptyQueueDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Outbox dispatch loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var now = DateTimeOffset.UtcNow;

        var messages = await dbContext.OutboxMessages
            .Where(message => message.ProcessedAt == null &&
                              (message.NextAttemptAt == null || message.NextAttemptAt <= now))
            .OrderBy(message => message.OccurredAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                await publisher.PublishAsync(
                    new CloudEventEnvelope(
                        "1.0",
                        message.Id,
                        "urn:sentinelpay:payments",
                        message.EventType,
                        message.OccurredAt,
                        $"payments/{message.AggregateId}",
                        "application/json",
                        message.Payload),
                    cancellationToken);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception exception)
            {
                message.AttemptCount++;
                message.LastError = exception.Message;
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(8, message.AttemptCount)));
                message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                _logger.LogWarning(
                    exception,
                    "Publishing outbox message {MessageId} failed on attempt {AttemptCount}.",
                    message.Id,
                    message.AttemptCount);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return messages.Count;
    }
}
