using Microsoft.Extensions.Logging;

namespace SentinelPay.Infrastructure.Outbox;

public sealed class LoggingEventPublisher : IEventPublisher
{
    private readonly ILogger<LoggingEventPublisher> _logger;

    public LoggingEventPublisher(ILogger<LoggingEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(CloudEventEnvelope cloudEvent, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Published {EventType} event {EventId} for {Subject}: {EventData}",
            cloudEvent.Type,
            cloudEvent.Id,
            cloudEvent.Subject,
            cloudEvent.Data);
        return Task.CompletedTask;
    }
}
