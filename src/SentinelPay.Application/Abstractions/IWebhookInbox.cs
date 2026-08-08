namespace SentinelPay.Application.Abstractions;

public interface IWebhookInbox
{
    Task<bool> ExistsAsync(string provider, string eventId, CancellationToken cancellationToken);
    void Add(string provider, string eventId, string eventType, string payloadHash, DateTimeOffset receivedAt);
}

public interface IWebhookSignatureVerifier
{
    bool IsValid(string provider, string payload, string signature);
}
