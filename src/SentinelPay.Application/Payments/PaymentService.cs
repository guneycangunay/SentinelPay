using System.Security.Cryptography;
using System.Text;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Application.Payments;

public sealed class PaymentService
{
    private static readonly TimeSpan LockExpiry = TimeSpan.FromSeconds(30);
    private readonly IPaymentStore _store;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly IDistributedLock _distributedLock;
    private readonly IOutboxWriter _outbox;
    private readonly IClock _clock;

    public PaymentService(
        IPaymentStore store,
        IPaymentGatewayResolver gatewayResolver,
        IDistributedLock distributedLock,
        IOutboxWriter outbox,
        IClock clock)
    {
        _store = store;
        _gatewayResolver = gatewayResolver;
        _distributedLock = distributedLock;
        _outbox = outbox;
        _clock = clock;
    }

    public async Task<PaymentResult> CreateAsync(
        CreatePaymentCommand command,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(command.IdempotencyKey);
        var requestHash = Hash(
            command.MerchantReference.Trim(),
            command.AmountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.Currency.Trim().ToUpperInvariant(),
            command.Provider.Trim().ToLowerInvariant(),
            command.PaymentMethodToken.Trim());

        await using var lease = await _distributedLock.AcquireAsync(
            $"payment:create:{command.IdempotencyKey}",
            LockExpiry,
            cancellationToken);

        var existing = await _store.GetByIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(existing.RequestHash),
                    Encoding.UTF8.GetBytes(requestHash)))
            {
                throw new IdempotencyConflictException(
                    "The idempotency key has already been used with a different payment request.");
            }

            return new PaymentResult(PaymentResponse.From(existing), true);
        }

        var gateway = _gatewayResolver.Resolve(command.Provider);
        var now = _clock.UtcNow;
        var payment = Payment.Create(
            command.MerchantReference,
            command.AmountMinor,
            command.Currency,
            gateway.Name,
            command.IdempotencyKey,
            requestHash,
            now);

        var authorization = await gateway.AuthorizeAsync(
            new GatewayAuthorizationRequest(
                payment.Id,
                payment.AmountMinor,
                payment.Currency,
                command.PaymentMethodToken,
                command.IdempotencyKey),
            cancellationToken);

        if (authorization.IsSuccessful)
        {
            payment.MarkAuthorized(
                authorization.ProviderReference ?? throw new InvalidOperationException("Gateway returned no reference."),
                _clock.UtcNow);
            _outbox.Add(
                "payment.authorized.v1",
                payment.Id,
                new
                {
                    payment.Id,
                    payment.MerchantReference,
                    payment.AmountMinor,
                    payment.Currency,
                    payment.Provider
                },
                _clock.UtcNow);
        }
        else
        {
            payment.MarkFailed(
                authorization.ErrorCode ?? "authorization_failed",
                authorization.ErrorMessage ?? "The provider declined the authorization.",
                _clock.UtcNow);
            _outbox.Add(
                "payment.failed.v1",
                payment.Id,
                new { payment.Id, payment.FailureCode, payment.FailureMessage },
                _clock.UtcNow);
        }

        await _store.AddAsync(payment, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        return new PaymentResult(PaymentResponse.From(payment), false);
    }

    public async Task<PaymentResponse> GetAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await _store.GetAsync(paymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(paymentId);
        return PaymentResponse.From(payment);
    }

    public async Task<PaymentResult> CaptureAsync(
        CapturePaymentCommand command,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(command.IdempotencyKey);
        await using var lease = await _distributedLock.AcquireAsync(
            $"payment:mutation:{command.PaymentId}",
            LockExpiry,
            cancellationToken);

        var payment = await _store.GetAsync(command.PaymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(command.PaymentId);

        if (payment.Status is PaymentStatus.Captured or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
        {
            return new PaymentResult(PaymentResponse.From(payment), true);
        }

        var gateway = _gatewayResolver.Resolve(payment.Provider);
        var result = await gateway.CaptureAsync(
            new GatewayCaptureRequest(
                payment.Id,
                payment.ProviderReference ?? throw new InvalidOperationException("Payment is missing a provider reference."),
                payment.AmountMinor,
                command.IdempotencyKey),
            cancellationToken);

        if (!result.IsSuccessful)
        {
            throw new PaymentProviderException(
                result.ErrorCode ?? "capture_failed",
                result.ErrorMessage ?? "The provider rejected the capture.");
        }

        payment.Capture(payment.AmountMinor, _clock.UtcNow);
        _outbox.Add(
            "payment.captured.v1",
            payment.Id,
            new { payment.Id, payment.CapturedAmountMinor, payment.Currency },
            _clock.UtcNow);
        await _store.SaveChangesAsync(cancellationToken);
        return new PaymentResult(PaymentResponse.From(payment), false);
    }

    public async Task<PaymentResult> RefundAsync(
        RefundPaymentCommand command,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(command.IdempotencyKey);
        var requestHash = Hash(
            command.PaymentId.ToString("N"),
            command.AmountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using var lease = await _distributedLock.AcquireAsync(
            $"payment:mutation:{command.PaymentId}",
            LockExpiry,
            cancellationToken);

        var payment = await _store.GetAsync(command.PaymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(command.PaymentId);

        var existing = payment.Refunds.SingleOrDefault(refund => refund.IdempotencyKey == command.IdempotencyKey);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(existing.RequestHash),
                    Encoding.UTF8.GetBytes(requestHash)))
            {
                throw new IdempotencyConflictException(
                    "The idempotency key has already been used with a different refund request.");
            }

            return new PaymentResult(PaymentResponse.From(payment), true);
        }

        var refundId = Guid.NewGuid();
        var gateway = _gatewayResolver.Resolve(payment.Provider);
        var result = await gateway.RefundAsync(
            new GatewayRefundRequest(
                payment.Id,
                refundId,
                payment.ProviderReference ?? throw new InvalidOperationException("Payment is missing a provider reference."),
                command.AmountMinor,
                command.IdempotencyKey),
            cancellationToken);

        if (!result.IsSuccessful)
        {
            throw new PaymentProviderException(
                result.ErrorCode ?? "refund_failed",
                result.ErrorMessage ?? "The provider rejected the refund.");
        }

        payment.RegisterRefund(
            refundId,
            command.AmountMinor,
            result.ProviderReference ?? throw new InvalidOperationException("Gateway returned no refund reference."),
            command.IdempotencyKey,
            requestHash,
            _clock.UtcNow);
        _outbox.Add(
            "payment.refunded.v1",
            payment.Id,
            new { payment.Id, RefundId = refundId, command.AmountMinor, payment.Currency },
            _clock.UtcNow);
        await _store.SaveChangesAsync(cancellationToken);
        return new PaymentResult(PaymentResponse.From(payment), false);
    }

    private static string Hash(params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', values)));
        return Convert.ToHexString(bytes);
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 128)
        {
            throw new IdempotencyConflictException("Idempotency key length must be between 8 and 128 characters.");
        }
    }
}
