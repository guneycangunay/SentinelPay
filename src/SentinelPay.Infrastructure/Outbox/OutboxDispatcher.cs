using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentinelPay.Application.Abstractions;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Outbox;

public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly Meter Meter = new("SentinelPay.Outbox");
    private static readonly Counter<long> Published = Meter.CreateCounter<long>("sentinelpay.outbox.published");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("sentinelpay.outbox.failed");
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("sentinelpay.outbox.dead_lettered");
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly IClock _clock;
    private readonly int _batchSize;
    private readonly int _maxAttempts;
    private readonly TimeSpan _emptyQueueDelay;
    private readonly TimeSpan _claimDuration;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IClock clock,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock;
        _batchSize = Math.Clamp(configuration.GetValue("Outbox:BatchSize", 20), 1, 200);
        _maxAttempts = Math.Clamp(configuration.GetValue("Outbox:MaxAttempts", 12), 1, 100);
        _emptyQueueDelay = TimeSpan.FromMilliseconds(
            Math.Clamp(configuration.GetValue("Outbox:PollIntervalMilliseconds", 2_000), 100, 60_000));
        _claimDuration = TimeSpan.FromSeconds(
            Math.Clamp(configuration.GetValue("Outbox:ClaimSeconds", 30), 5, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimedIds = await ClaimBatchAsync(stoppingToken);
                if (claimedIds.Count == 0)
                {
                    await Task.Delay(_emptyQueueDelay, stoppingToken);
                    continue;
                }

                foreach (var messageId in claimedIds)
                {
                    await DispatchAsync(messageId, stoppingToken);
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

    private async Task<IReadOnlyList<Guid>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = _clock.UtcNow;

        var messages = await dbContext.OutboxMessages
            .FromSqlInterpolated($$"""
                SELECT *
                FROM sentinelpay.outbox_messages
                WHERE "ProcessedAt" IS NULL
                  AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {{now}})
                  AND ("LockedUntil" IS NULL OR "LockedUntil" <= {{now}})
                  AND "DeadLetteredAt" IS NULL
                ORDER BY "OccurredAt"
                FOR UPDATE SKIP LOCKED
                LIMIT {{_batchSize}}
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            message.LockedBy = _workerId;
            message.LockedUntil = now.Add(_claimDuration);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Select(message => message.Id).ToArray();
    }

    private async Task DispatchAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var message = await dbContext.OutboxMessages.SingleOrDefaultAsync(
            item => item.Id == messageId && item.LockedBy == _workerId,
            cancellationToken);
        if (message is null)
        {
            return;
        }

        try
        {
            await publisher.PublishAsync(
                new CloudEventEnvelope(
                    "1.0",
                    message.Id,
                    "urn:sentinelpay:payments",
                    message.EventType,
                    message.OccurredAt,
                    $"aggregates/{message.AggregateId}",
                    "application/json",
                    message.Payload),
                cancellationToken);
            message.ProcessedAt = _clock.UtcNow;
            message.NextAttemptAt = null;
            message.LastError = null;
            message.LockedBy = null;
            message.LockedUntil = null;
            Published.Add(1, new KeyValuePair<string, object?>("event.type", message.EventType));
        }
        catch (Exception exception)
        {
            message.AttemptCount++;
            message.LastError = exception.Message[..Math.Min(exception.Message.Length, 2_000)];
            if (message.AttemptCount >= _maxAttempts)
            {
                message.DeadLetteredAt = _clock.UtcNow;
                message.NextAttemptAt = null;
                DeadLettered.Add(1, new KeyValuePair<string, object?>("event.type", message.EventType));
                _logger.LogError(
                    exception,
                    "Outbox message {MessageId} was dead-lettered after {AttemptCount} attempts.",
                    message.Id,
                    message.AttemptCount);
            }
            else
            {
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(8, message.AttemptCount)));
                message.NextAttemptAt = _clock.UtcNow.AddSeconds(seconds);
                _logger.LogWarning(
                    exception,
                    "Publishing outbox message {MessageId} failed on attempt {AttemptCount}.",
                    message.Id,
                    message.AttemptCount);
            }

            message.LockedBy = null;
            message.LockedUntil = null;
            Failed.Add(1, new KeyValuePair<string, object?>("event.type", message.EventType));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
