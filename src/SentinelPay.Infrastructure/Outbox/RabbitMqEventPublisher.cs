using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace SentinelPay.Infrastructure.Outbox;

public sealed class RabbitMqEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqEventPublisher(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task PublishAsync(CloudEventEnvelope cloudEvent, CancellationToken cancellationToken)
    {
        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            var channel = await EnsureChannelAsync(cancellationToken);
            using var dataDocument = JsonDocument.Parse(cloudEvent.Data);
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                specversion = cloudEvent.SpecVersion,
                id = cloudEvent.Id,
                source = cloudEvent.Source,
                type = cloudEvent.Type,
                time = cloudEvent.Time,
                subject = cloudEvent.Subject,
                datacontenttype = cloudEvent.DataContentType,
                data = dataDocument.RootElement
            });
            var properties = new BasicProperties
            {
                AppId = "SentinelPay.Api",
                ContentType = "application/cloudevents+json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = cloudEvent.Id.ToString(),
                Type = cloudEvent.Type,
                Timestamp = new AmqpTimestamp(cloudEvent.Time.ToUnixTimeSeconds())
            };

            await channel.BasicPublishAsync(
                _configuration["Messaging:Exchange"] ?? "sentinelpay.events",
                cloudEvent.Type,
                mandatory: true,
                properties,
                body,
                cancellationToken);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync();
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        _channelLock.Dispose();
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        var connectionString = _configuration.GetConnectionString("RabbitMq")
            ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is required.");
        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            ClientProvidedName = $"sentinelpay-outbox-{Environment.MachineName}"
        };
        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        var exchange = _configuration["Messaging:Exchange"] ?? "sentinelpay.events";
        var auditQueue = _configuration["Messaging:AuditQueue"] ?? "sentinelpay.audit.v2";
        var deadLetterExchange = _configuration["Messaging:DeadLetterExchange"] ?? "sentinelpay.dead-letter";
        var deadLetterQueue = _configuration["Messaging:AuditDeadLetterQueue"] ?? "sentinelpay.audit.dlq";
        const string deadLetterRoutingKey = "audit.poison";
        await _channel.ExchangeDeclareAsync(
            exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await _channel.ExchangeDeclareAsync(
            deadLetterExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(
            deadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        await _channel.QueueBindAsync(
            deadLetterQueue,
            deadLetterExchange,
            deadLetterRoutingKey,
            arguments: null,
            cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(
            auditQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = deadLetterExchange,
                ["x-dead-letter-routing-key"] = deadLetterRoutingKey
            },
            cancellationToken: cancellationToken);
        await _channel.QueueBindAsync(
            auditQueue,
            exchange,
            "#",
            arguments: null,
            cancellationToken: cancellationToken);
        return _channel;
    }
}
