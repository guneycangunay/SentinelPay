using Microsoft.EntityFrameworkCore;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Persistence;

public sealed class WebhookInbox : IWebhookInbox
{
    private readonly SentinelPayDbContext _dbContext;

    public WebhookInbox(SentinelPayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> ExistsAsync(string provider, string eventId, CancellationToken cancellationToken) =>
        _dbContext.WebhookReceipts.AnyAsync(
            receipt => receipt.Provider == provider && receipt.EventId == eventId,
            cancellationToken);

    public void Add(string provider, string eventId, string eventType, string payloadHash, DateTimeOffset receivedAt)
    {
        _dbContext.WebhookReceipts.Add(new WebhookReceipt
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            EventId = eventId,
            EventType = eventType,
            PayloadHash = payloadHash,
            ReceivedAt = receivedAt
        });
    }
}
