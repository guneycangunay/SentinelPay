using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Application.Payments;

public sealed class WebhookService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IPaymentStore _paymentStore;
    private readonly IWebhookInbox _inbox;
    private readonly IWebhookSignatureVerifier _signatureVerifier;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly IOutboxWriter _outbox;
    private readonly ILedgerWriter _ledger;
    private readonly IDistributedLock _distributedLock;
    private readonly IClock _clock;

    public WebhookService(
        IPaymentStore paymentStore,
        IWebhookInbox inbox,
        IWebhookSignatureVerifier signatureVerifier,
        IPaymentGatewayResolver gatewayResolver,
        IOutboxWriter outbox,
        ILedgerWriter ledger,
        IDistributedLock distributedLock,
        IClock clock)
    {
        _paymentStore = paymentStore;
        _inbox = inbox;
        _signatureVerifier = signatureVerifier;
        _gatewayResolver = gatewayResolver;
        _outbox = outbox;
        _ledger = ledger;
        _distributedLock = distributedLock;
        _clock = clock;
    }

    public async Task<WebhookResult> HandleAsync(
        string provider,
        string payload,
        string signature,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = _gatewayResolver.Resolve(provider).Name;
        if (!_signatureVerifier.IsValid(normalizedProvider, payload, signature))
        {
            throw new InvalidWebhookSignatureException();
        }

        WebhookPayload webhook;
        try
        {
            webhook = JsonSerializer.Deserialize<WebhookPayload>(payload, SerializerOptions)
                ?? throw new InvalidWebhookPayloadException("Webhook body is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidWebhookPayloadException($"Webhook JSON is invalid: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(webhook.Id) ||
            string.IsNullOrWhiteSpace(webhook.Type) ||
            string.IsNullOrWhiteSpace(webhook.ProviderReference))
        {
            throw new InvalidWebhookPayloadException("Webhook id, type and providerReference are required.");
        }

        if (webhook.Id.Length > 160 ||
            webhook.Type.Length > 160 ||
            webhook.ProviderReference.Length > 120 ||
            (webhook.ProviderOperationReference is not null && webhook.ProviderOperationReference.Length > 120) ||
            (webhook.AmountMinor is not null && webhook.AmountMinor <= 0) ||
            (webhook.ErrorCode is not null && webhook.ErrorCode.Length > 80))
        {
            throw new InvalidWebhookPayloadException("Webhook field length exceeds the provider contract.");
        }

        await using var lease = await _distributedLock.AcquireAsync(
            $"webhook:{normalizedProvider}:{webhook.Id}",
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (await _inbox.ExistsAsync(normalizedProvider, webhook.Id, cancellationToken))
        {
            return new WebhookResult(true);
        }

        var payment = await _paymentStore.GetByProviderReferenceAsync(
            normalizedProvider,
            webhook.ProviderReference,
            cancellationToken) ?? throw new InvalidWebhookPayloadException(
                "No payment matches the webhook provider reference.");
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var operationIdentity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{normalizedProvider}:{webhook.Id}")));
        var operation = payment.StartOperation(
            PaymentOperationType.Reconcile,
            $"webhook:{operationIdentity}",
            payloadHash,
            _clock.UtcNow);

        switch (webhook.Type.ToLowerInvariant())
        {
            case "payment.authentication_succeeded" when payment.Status == PaymentStatus.RequiresAction:
                payment.MarkAuthorized(webhook.ProviderReference, _clock.UtcNow.AddDays(7), _clock.UtcNow);
                break;
            case "payment.authentication_failed" when payment.Status == PaymentStatus.RequiresAction:
                payment.MarkFailed(
                    webhook.ErrorCode ?? "authentication_failed",
                    webhook.ErrorMessage ?? "The issuer challenge was not completed.",
                    _clock.UtcNow);
                break;
            case "payment.captured" when payment.Status is PaymentStatus.Authorized or PaymentStatus.PartiallyCaptured:
            {
                var captureAmount = webhook.AmountMinor ?? payment.RemainingAuthorizedAmountMinor;
                var capture = payment.RegisterCapture(
                    operation.Id,
                    captureAmount,
                    webhook.ProviderOperationReference ?? $"whcap_{operationIdentity[..32].ToLowerInvariant()}",
                    operation.IdempotencyKey,
                    payloadHash,
                    _clock.UtcNow);
                await _ledger.RecordCaptureAsync(payment, capture, _clock.UtcNow, cancellationToken);
                break;
            }
            case "payment.failed" when payment.Status is PaymentStatus.Pending or PaymentStatus.RequiresAction or PaymentStatus.Authorized:
                payment.MarkFailed(
                    webhook.ErrorCode ?? "provider_webhook_failure",
                    webhook.ErrorMessage ?? "The provider reported that the payment failed.",
                    _clock.UtcNow);
                break;
            case "payment.authentication_succeeded" or
                 "payment.authentication_failed" or
                 "payment.captured" or
                 "payment.failed":
                break; // A valid duplicate state transition is acknowledged and recorded.
            default:
                throw new InvalidWebhookPayloadException($"Webhook event type '{webhook.Type}' is not supported.");
        }

        operation.Succeed(webhook.ProviderReference, _clock.UtcNow);

        _inbox.Add(
            normalizedProvider,
            webhook.Id,
            webhook.Type,
            payloadHash,
            _clock.UtcNow);
        _outbox.Add(
            "provider.webhook-processed.v2",
            payment.Id,
            new
            {
                PaymentId = payment.Id,
                payment.MerchantId,
                Provider = normalizedProvider,
                webhook.Id,
                webhook.Type,
                payment.Status
            },
            _clock.UtcNow);
        await _paymentStore.SaveChangesAsync(cancellationToken);
        return new WebhookResult(false);
    }

    private sealed record WebhookPayload(
        string Id,
        string Type,
        string ProviderReference,
        string? ProviderOperationReference,
        long? AmountMinor,
        string? ErrorCode,
        string? ErrorMessage);
}

public sealed record WebhookResult(bool IsReplay);
