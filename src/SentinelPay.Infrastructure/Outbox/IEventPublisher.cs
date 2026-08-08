namespace SentinelPay.Infrastructure.Outbox;

public interface IEventPublisher
{
    Task PublishAsync(CloudEventEnvelope cloudEvent, CancellationToken cancellationToken);
}

public sealed record CloudEventEnvelope(
    string SpecVersion,
    Guid Id,
    string Source,
    string Type,
    DateTimeOffset Time,
    string Subject,
    string DataContentType,
    string Data);
