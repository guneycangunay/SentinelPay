using System.Security.Cryptography;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SentinelPay.Application.Abstractions;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Outbox;

public sealed class AuditEventConsumer : BackgroundService
{
    private const string ConsumerName = "sentinelpay-audit-v1";
    private static readonly Meter Meter = new("SentinelPay.Outbox");
    private static readonly Counter<long> Consumed = Meter.CreateCounter<long>("sentinelpay.consumer.consumed");
    private static readonly Counter<long> Deduplicated = Meter.CreateCounter<long>("sentinelpay.consumer.deduplicated");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("sentinelpay.consumer.rejected");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly ILogger<AuditEventConsumer> _logger;

    public AuditEventConsumer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IClock clock,
        ILogger<AuditEventConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilStoppedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Audit consumer connection failed; reconnecting after a bounded delay.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task ConsumeUntilStoppedAsync(CancellationToken stoppingToken)
    {
        var connectionString = _configuration.GetConnectionString("RabbitMq")
            ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is required.");
        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            ClientProvidedName = $"sentinelpay-audit-{Environment.MachineName}"
        };
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        var queue = await DeclareTopologyAsync(channel, stoppingToken);
        await channel.BasicQosAsync(0, 16, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
            await HandleDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(
            queue: queue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation("Audit event consumer is listening on {Queue}.", queue);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        var body = delivery.Body.ToArray();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var eventId = RequireString(root, "id", 160);
            var eventType = RequireString(root, "type", 160);
            var subject = root.TryGetProperty("subject", out var subjectElement)
                ? subjectElement.GetString()
                : null;
            Guid? aggregateId = TryParseAggregateId(subject);
            var payloadHash = Convert.ToHexString(SHA256.HashData(body));

            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
            if (await dbContext.ConsumedEvents.AnyAsync(
                    item => item.Consumer == ConsumerName && item.EventId == eventId,
                    stoppingToken))
            {
                Deduplicated.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                return;
            }

            await dbContext.ConsumedEvents.AddAsync(
                new ConsumedEvent(
                    Guid.NewGuid(),
                    ConsumerName,
                    eventId,
                    eventType,
                    aggregateId,
                    payloadHash,
                    _clock.UtcNow),
                stoppingToken);
            await dbContext.SaveChangesAsync(stoppingToken);
            Consumed.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Rejecting malformed CloudEvent delivery {DeliveryTag}.", delivery.DeliveryTag);
            Rejected.Add(1, new KeyValuePair<string, object?>("reason", "malformed_json"));
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
        }
        catch (InvalidDataException exception)
        {
            _logger.LogWarning(exception, "Rejecting invalid CloudEvent delivery {DeliveryTag}.", delivery.DeliveryTag);
            Rejected.Add(1, new KeyValuePair<string, object?>("reason", "invalid_envelope"));
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Audit event handling failed; delivery {DeliveryTag} will be retried.", delivery.DeliveryTag);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
        }
    }

    private async Task<string> DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var exchange = _configuration["Messaging:Exchange"] ?? "sentinelpay.events";
        var queue = _configuration["Messaging:AuditQueue"] ?? "sentinelpay.audit.v2";
        var deadLetterExchange = _configuration["Messaging:DeadLetterExchange"] ?? "sentinelpay.dead-letter";
        var deadLetterQueue = _configuration["Messaging:AuditDeadLetterQueue"] ?? "sentinelpay.audit.dlq";
        const string deadLetterRoutingKey = "audit.poison";

        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(deadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(deadLetterQueue, deadLetterExchange, deadLetterRoutingKey, arguments: null, cancellationToken: cancellationToken);
        var queueArguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = deadLetterExchange,
            ["x-dead-letter-routing-key"] = deadLetterRoutingKey
        };
        await channel.QueueDeclareAsync(
            queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue, exchange, "#", arguments: null, cancellationToken: cancellationToken);
        return queue;
    }

    private static string RequireString(JsonElement root, string propertyName, int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()) ||
            element.GetString()!.Length > maximumLength)
        {
            throw new InvalidDataException($"CloudEvent property '{propertyName}' is missing or invalid.");
        }

        return element.GetString()!;
    }

    private static Guid? TryParseAggregateId(string? subject)
    {
        var value = subject?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return Guid.TryParse(value, out var aggregateId) ? aggregateId : null;
    }
}
