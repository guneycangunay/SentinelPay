using System.Text.Json;
using SentinelPay.Application.Abstractions;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Outbox;

public sealed class EfOutboxWriter : IOutboxWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SentinelPayDbContext _dbContext;

    public EfOutboxWriter(SentinelPayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(string eventType, Guid aggregateId, object payload, DateTimeOffset occurredAt)
    {
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            AggregateId = aggregateId,
            Payload = JsonSerializer.Serialize(payload, SerializerOptions),
            OccurredAt = occurredAt
        });
    }
}
