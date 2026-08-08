namespace SentinelPay.Infrastructure.Persistence;

public sealed class ConsumedEvent
{
    private ConsumedEvent()
    {
    }

    public ConsumedEvent(
        Guid id,
        string consumer,
        string eventId,
        string eventType,
        Guid? aggregateId,
        string payloadSha256,
        DateTimeOffset receivedAt)
    {
        Id = id;
        Consumer = consumer;
        EventId = eventId;
        EventType = eventType;
        AggregateId = aggregateId;
        PayloadSha256 = payloadSha256;
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; private set; }
    public string Consumer { get; private set; } = string.Empty;
    public string EventId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public Guid? AggregateId { get; private set; }
    public string PayloadSha256 { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }
}
