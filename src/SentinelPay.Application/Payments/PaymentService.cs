using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Application.Payments;

public sealed class PaymentService
{
    private static readonly TimeSpan LockExpiry = TimeSpan.FromSeconds(30);
    private readonly IPaymentStore _store;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly IDistributedLock _distributedLock;
    private readonly IOutboxWriter _outbox;
    private readonly ILedgerWriter _ledger;
    private readonly IClock _clock;

    public PaymentService(
        IPaymentStore store,
        IPaymentGatewayResolver gatewayResolver,
        IDistributedLock distributedLock,
        IOutboxWriter outbox,
        ILedgerWriter ledger,
        IClock clock)
    {
        _store = store;
        _gatewayResolver = gatewayResolver;
        _distributedLock = distributedLock;
        _outbox = outbox;
        _ledger = ledger;
        _clock = clock;
    }

    public async Task<PaymentResult> CreateAsync(
        CreatePaymentCommand command,
        CancellationToken cancellationToken)
    {
        ValidateMerchant(command.MerchantId);
        var idempotencyKey = NormalizeIdempotencyKey(command.IdempotencyKey);
        ValidateCreateCommand(command);
        var requestHash = Hash(
            command.MerchantId.ToString("N"),
            command.MerchantReference.Trim(),
            command.AmountMinor.ToString(CultureInfo.InvariantCulture),
            command.Currency.Trim().ToUpperInvariant(),
            command.Provider.Trim().ToLowerInvariant(),
            command.PaymentMethodToken.Trim());

        await using var lease = await _distributedLock.AcquireAsync(
            $"payment:create:{command.MerchantId:N}:{idempotencyKey}",
            LockExpiry,
            cancellationToken);

        var payment = await _store.GetByIdempotencyKeyAsync(
            command.MerchantId,
            idempotencyKey,
            cancellationToken);
        PaymentOperation operation;

        if (payment is not null)
        {
            ValidateHash(payment.RequestHash, requestHash, "payment request");
            operation = payment.Operations.Single(item => item.Type == PaymentOperationType.Authorize);
            if (operation.Status != PaymentOperationStatus.Started)
            {
                return new PaymentResult(PaymentResponse.From(payment), true);
            }
        }
        else
        {
            var gateway = _gatewayResolver.Resolve(command.Provider);
            payment = Payment.Create(
                command.MerchantId,
                command.MerchantReference,
                command.AmountMinor,
                command.Currency,
                gateway.Name,
                idempotencyKey,
                requestHash,
                _clock.UtcNow);
            operation = payment.StartOperation(
                PaymentOperationType.Authorize,
                idempotencyKey,
                requestHash,
                _clock.UtcNow);
            await _store.AddAsync(payment, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }

        var provider = _gatewayResolver.Resolve(payment.Provider);
        using var activity = PaymentTelemetry.ActivitySource.StartActivity("payment.authorize");
        activity?.SetTag("payment.provider", payment.Provider);
        activity?.SetTag("payment.currency", payment.Currency);
        activity?.SetTag("payment.id", payment.Id);
        var stopwatch = Stopwatch.StartNew();
        var authorization = await InvokeProviderAsync(() => provider.AuthorizeAsync(
                new GatewayAuthorizationRequest(
                    payment.Id,
                    payment.AmountMinor,
                    payment.Currency,
                    command.PaymentMethodToken,
                    idempotencyKey),
                cancellationToken))
            .ConfigureAwait(false);
        PaymentTelemetry.RecordProviderLatency(payment.Provider, "authorize", stopwatch.Elapsed);
        PaymentTelemetry.RecordAuthorization(
            payment.Provider,
            payment.Currency,
            payment.AmountMinor,
            authorization.IsSuccessful);

        if (authorization.IsSuccessful)
        {
            var providerReference = authorization.ProviderReference
                ?? throw new InvalidOperationException("Gateway returned no reference.");
            payment.MarkAuthorized(providerReference, _clock.UtcNow);
            operation.Succeed(providerReference, _clock.UtcNow);
            _outbox.Add(
                "payment.authorized.v2",
                payment.Id,
                new
                {
                    payment.Id,
                    payment.MerchantId,
                    payment.MerchantReference,
                    payment.AmountMinor,
                    payment.Currency,
                    payment.Provider
                },
                _clock.UtcNow);
        }
        else
        {
            var code = authorization.ErrorCode ?? "authorization_failed";
            var message = authorization.ErrorMessage ?? "The provider declined the authorization.";
            payment.MarkFailed(code, message, _clock.UtcNow);
            operation.Fail(code, message, _clock.UtcNow);
            _outbox.Add(
                "payment.failed.v2",
                payment.Id,
                new { payment.Id, payment.MerchantId, payment.FailureCode, payment.FailureMessage },
                _clock.UtcNow);
        }

        await _store.SaveChangesAsync(cancellationToken);
        return new PaymentResult(PaymentResponse.From(payment), false);
    }

    public async Task<PaymentResponse> GetAsync(
        Guid merchantId,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        var payment = await _store.GetAsync(merchantId, paymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(paymentId);
        return PaymentResponse.From(payment);
    }

    public async Task<PaymentResult> CaptureAsync(
        CapturePaymentCommand command,
        CancellationToken cancellationToken)
    {
        ValidateMerchant(command.MerchantId);
        var idempotencyKey = NormalizeIdempotencyKey(command.IdempotencyKey);
        var requestHash = Hash(command.MerchantId.ToString("N"), command.PaymentId.ToString("N"), "capture-full");

        await using var lease = await _distributedLock.AcquireAsync(
            $"payment:mutation:{command.MerchantId:N}:{command.PaymentId:N}",
            LockExpiry,
            cancellationToken);

        var payment = await _store.GetAsync(command.MerchantId, command.PaymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(command.PaymentId);
        var operation = FindOperation(payment, PaymentOperationType.Capture, idempotencyKey);

        if (operation is not null)
        {
            ValidateHash(operation.RequestHash, requestHash, "capture request");
            if (operation.Status == PaymentOperationStatus.Succeeded)
            {
                return new PaymentResult(PaymentResponse.From(payment), true);
            }

            ThrowIfFailed(operation);
        }
        else
        {
            if (payment.Status != PaymentStatus.Authorized)
            {
                throw new DomainException(
                    $"A payment in '{payment.Status}' state cannot start a new capture operation.");
            }

            operation = payment.StartOperation(
                PaymentOperationType.Capture,
                idempotencyKey,
                requestHash,
                _clock.UtcNow);
            await _store.SaveChangesAsync(cancellationToken);
        }

        var gateway = _gatewayResolver.Resolve(payment.Provider);
        using var activity = PaymentTelemetry.ActivitySource.StartActivity("payment.capture");
        activity?.SetTag("payment.provider", payment.Provider);
        activity?.SetTag("payment.id", payment.Id);
        var stopwatch = Stopwatch.StartNew();
        var result = await InvokeProviderAsync(() => gateway.CaptureAsync(
                new GatewayCaptureRequest(
                    payment.Id,
                    payment.ProviderReference ?? throw new InvalidOperationException("Payment is missing a provider reference."),
                    payment.AmountMinor,
                    idempotencyKey),
                cancellationToken))
            .ConfigureAwait(false);
        PaymentTelemetry.RecordProviderLatency(payment.Provider, "capture", stopwatch.Elapsed);

        if (!result.IsSuccessful)
        {
            var code = result.ErrorCode ?? "capture_failed";
            var message = result.ErrorMessage ?? "The provider rejected the capture.";
            operation.Fail(code, message, _clock.UtcNow);
            await _store.SaveChangesAsync(cancellationToken);
            throw new PaymentProviderException(code, message);
        }

        payment.Capture(payment.AmountMinor, _clock.UtcNow);
        PaymentTelemetry.RecordCapture(payment.Provider, payment.Currency, payment.AmountMinor);
        operation.Succeed(payment.ProviderReference, _clock.UtcNow);
        await _ledger.RecordCaptureAsync(payment, _clock.UtcNow, cancellationToken);
        _outbox.Add(
            "payment.captured.v2",
            payment.Id,
            new { payment.Id, payment.MerchantId, payment.CapturedAmountMinor, payment.Currency },
            _clock.UtcNow);
        await _store.SaveChangesAsync(cancellationToken);
        return new PaymentResult(PaymentResponse.From(payment), false);
    }

    public async Task<PaymentResult> RefundAsync(
        RefundPaymentCommand command,
        CancellationToken cancellationToken)
    {
        ValidateMerchant(command.MerchantId);
        var idempotencyKey = NormalizeIdempotencyKey(command.IdempotencyKey);
        var requestHash = Hash(
            command.MerchantId.ToString("N"),
            command.PaymentId.ToString("N"),
            command.AmountMinor.ToString(CultureInfo.InvariantCulture));

        await using var lease = await _distributedLock.AcquireAsync(
            $"payment:mutation:{command.MerchantId:N}:{command.PaymentId:N}",
            LockExpiry,
            cancellationToken);

        var payment = await _store.GetAsync(command.MerchantId, command.PaymentId, cancellationToken)
            ?? throw new PaymentNotFoundException(command.PaymentId);
        var operation = FindOperation(payment, PaymentOperationType.Refund, idempotencyKey);

        if (operation is not null)
        {
            ValidateHash(operation.RequestHash, requestHash, "refund request");
            if (operation.Status == PaymentOperationStatus.Succeeded)
            {
                return new PaymentResult(PaymentResponse.From(payment), true);
            }

            ThrowIfFailed(operation);
        }
        else
        {
            payment.EnsureCanRefund(command.AmountMinor);
            operation = payment.StartOperation(
                PaymentOperationType.Refund,
                idempotencyKey,
                requestHash,
                _clock.UtcNow);
            await _store.SaveChangesAsync(cancellationToken);
        }

        var refundId = Guid.NewGuid();
        var gateway = _gatewayResolver.Resolve(payment.Provider);
        using var activity = PaymentTelemetry.ActivitySource.StartActivity("payment.refund");
        activity?.SetTag("payment.provider", payment.Provider);
        activity?.SetTag("payment.id", payment.Id);
        var stopwatch = Stopwatch.StartNew();
        var result = await InvokeProviderAsync(() => gateway.RefundAsync(
                new GatewayRefundRequest(
                    payment.Id,
                    refundId,
                    payment.ProviderReference ?? throw new InvalidOperationException("Payment is missing a provider reference."),
                    command.AmountMinor,
                    idempotencyKey),
                cancellationToken))
            .ConfigureAwait(false);
        PaymentTelemetry.RecordProviderLatency(payment.Provider, "refund", stopwatch.Elapsed);

        if (!result.IsSuccessful)
        {
            var code = result.ErrorCode ?? "refund_failed";
            var message = result.ErrorMessage ?? "The provider rejected the refund.";
            operation.Fail(code, message, _clock.UtcNow);
            await _store.SaveChangesAsync(cancellationToken);
            throw new PaymentProviderException(code, message);
        }

        var providerReference = result.ProviderReference
            ?? throw new InvalidOperationException("Gateway returned no refund reference.");
        var refund = payment.RegisterRefund(
            refundId,
            command.AmountMinor,
            providerReference,
            idempotencyKey,
            requestHash,
            _clock.UtcNow);
        operation.Succeed(providerReference, _clock.UtcNow);
        PaymentTelemetry.RecordRefund(payment.Provider, payment.Currency, command.AmountMinor);
        await _ledger.RecordRefundAsync(payment, refund, _clock.UtcNow, cancellationToken);
        _outbox.Add(
            "payment.refunded.v2",
            payment.Id,
            new { payment.Id, payment.MerchantId, RefundId = refundId, command.AmountMinor, payment.Currency },
            _clock.UtcNow);
        await _store.SaveChangesAsync(cancellationToken);
        return new PaymentResult(PaymentResponse.From(payment), false);
    }

    private static PaymentOperation? FindOperation(
        Payment payment,
        PaymentOperationType type,
        string idempotencyKey) =>
        payment.Operations.SingleOrDefault(operation =>
            operation.Type == type && operation.IdempotencyKey == idempotencyKey);

    private static void ThrowIfFailed(PaymentOperation operation)
    {
        if (operation.Status == PaymentOperationStatus.Failed)
        {
            throw new PaymentProviderException(
                operation.ErrorCode ?? "provider_operation_failed",
                operation.ErrorMessage ?? "The provider previously rejected this operation.");
        }
    }

    private static async Task<T> InvokeProviderAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new PaymentProviderUnavailableException(exception);
        }
        catch (TimeoutException exception)
        {
            throw new PaymentProviderUnavailableException(exception);
        }
    }

    private static string Hash(params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', values)));
        return Convert.ToHexString(bytes);
    }

    private static void ValidateHash(string storedHash, string requestHash, string operationName)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(storedHash),
                Encoding.UTF8.GetBytes(requestHash)))
        {
            throw new IdempotencyConflictException(
                $"The idempotency key has already been used with a different {operationName}.");
        }
    }

    private static void ValidateMerchant(Guid merchantId)
    {
        if (merchantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Merchant identity is required.");
        }
    }

    private static string NormalizeIdempotencyKey(string idempotencyKey)
    {
        var normalized = idempotencyKey?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 128)
        {
            throw new IdempotencyConflictException("Idempotency key length must be between 8 and 128 characters.");
        }

        return normalized;
    }

    private static void ValidateCreateCommand(CreatePaymentCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.MerchantReference) || command.MerchantReference.Length > 100)
        {
            throw new ArgumentException("Merchant reference is required and cannot exceed 100 characters.");
        }

        if (string.IsNullOrWhiteSpace(command.Currency) ||
            command.Currency.Length != 3 ||
            command.Currency.Any(character => !char.IsLetter(character)))
        {
            throw new ArgumentException("Currency must be a three-letter ISO 4217 code.");
        }

        if (string.IsNullOrWhiteSpace(command.Provider) || command.Provider.Length > 40)
        {
            throw new ArgumentException("A valid payment provider is required.");
        }

        if (string.IsNullOrWhiteSpace(command.PaymentMethodToken) || command.PaymentMethodToken.Length > 256)
        {
            throw new ArgumentException("A valid tokenized payment method is required.");
        }
    }
}
