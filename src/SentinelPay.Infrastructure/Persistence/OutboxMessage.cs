namespace SentinelPay.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public Guid AggregateId { get; init; }
    public string Payload { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
}
