namespace SentinelPay.Infrastructure.Persistence;

public sealed class WebhookReceipt
{
    public Guid Id { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string PayloadHash { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
}
