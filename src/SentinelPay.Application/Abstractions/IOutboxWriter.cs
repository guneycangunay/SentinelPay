namespace SentinelPay.Application.Abstractions;

public interface IOutboxWriter
{
    void Add(string eventType, Guid aggregateId, object payload, DateTimeOffset occurredAt);
}
