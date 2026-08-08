using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
var app = builder.Build();

var authorizations = new ConcurrentDictionary<string, AuthorizationState>(StringComparer.Ordinal);
var responses = new ConcurrentDictionary<string, StoredResponse>(StringComparer.Ordinal);
var transientAttempts = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

app.MapPost("/v1/authorizations", (
    AuthorizationRequest request,
    HttpRequest httpRequest) =>
{
    var idempotencyKey = RequireIdempotencyKey(httpRequest);
    if (request.PaymentId == Guid.Empty ||
        request.AmountMinor <= 0 ||
        !IsCurrency(request.Currency) ||
        string.IsNullOrWhiteSpace(request.PaymentMethodToken) ||
        request.PaymentMethodToken.Length > 256)
    {
        return Results.UnprocessableEntity(new
        {
            code = "authorization_payload_invalid",
            message = "Payment id, positive amount, ISO currency and a tokenized payment method are required."
        });
    }

    var operationKey = $"authorize:{idempotencyKey}";
    var fingerprint = Fingerprint(request);
    if (ReplayOrConflict(responses, operationKey, fingerprint) is { } replay)
    {
        return replay;
    }

    if (request.PaymentMethodToken.Equals("tok_http_rate_limited", StringComparison.OrdinalIgnoreCase) &&
        transientAttempts.TryAdd(operationKey, 0))
    {
        httpRequest.HttpContext.Response.Headers["Retry-After"] = "1";
        return Results.Json(
            new { code = "rate_limited", message = "Retry this idempotent operation after one second." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (request.PaymentMethodToken.Equals("tok_http_timeout", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new { code = "provider_timeout", message = "Fault fixture requested a gateway timeout." },
            statusCode: StatusCodes.Status504GatewayTimeout);
    }

    if (request.PaymentMethodToken.Equals("tok_http_declined", StringComparison.OrdinalIgnoreCase))
    {
        return Store(
            responses,
            operationKey,
            fingerprint,
            StatusCodes.Status402PaymentRequired,
            new { code = "card_declined", message = "The issuer declined the authorization." });
    }

    var providerReference = DeterministicReference("auth", request.PaymentId, idempotencyKey);
    var requiresAction = request.PaymentMethodToken.Equals("tok_http_3ds", StringComparison.OrdinalIgnoreCase);
    authorizations[providerReference] = new AuthorizationState(
        request.PaymentId,
        request.AmountMinor,
        request.Currency.ToUpperInvariant(),
        requiresAction ? "requires_action" : "authorized");
    object payload = requiresAction
        ? new
        {
            state = "requires_action",
            provider_reference = providerReference,
            next_action = new
            {
                type = "redirect",
                url = $"https://sandbox.sentinelpay.dev/3ds/{providerReference}",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(10)
            }
        }
        : new { state = "authorized", provider_reference = providerReference };
    return Store(responses, operationKey, fingerprint, StatusCodes.Status200OK, payload);
});

app.MapPost("/v1/authorizations/{providerReference}/authenticate", (
    string providerReference,
    AuthenticationRequest request,
    HttpRequest httpRequest) =>
{
    var idempotencyKey = RequireIdempotencyKey(httpRequest);
    var operationKey = $"authenticate:{providerReference}:{idempotencyKey}";
    var fingerprint = Fingerprint(request);
    if (ReplayOrConflict(responses, operationKey, fingerprint) is { } replay)
    {
        return replay;
    }

    if (!authorizations.TryGetValue(providerReference, out var authorization))
    {
        return Results.NotFound(new { code = "authorization_not_found", message = "Authorization does not exist." });
    }

    if (request.PaymentId != authorization.PaymentId || request.AmountMinor != authorization.AuthorizedAmountMinor)
    {
        return Results.Conflict(new { code = "idempotency_payload_mismatch", message = "Authentication payload does not match the authorization." });
    }

    lock (authorization)
    {
        if (ReplayOrConflict(responses, operationKey, fingerprint) is { } lockedReplay)
        {
            return lockedReplay;
        }

        if (authorization.State != "requires_action")
        {
            return Results.UnprocessableEntity(new
            {
                code = "authentication_not_required",
                message = "Only an authorization awaiting cardholder action can be authenticated."
            });
        }

        if (string.IsNullOrWhiteSpace(request.AuthenticationResultToken) ||
            request.AuthenticationResultToken.Length > 512)
        {
            return Results.UnprocessableEntity(new
            {
                code = "authentication_result_invalid",
                message = "A bounded authentication result token is required."
            });
        }

        if (request.AuthenticationResultToken.Equals("auth_failed", StringComparison.OrdinalIgnoreCase))
        {
            authorization.State = "failed";
            return Store(
                responses,
                operationKey,
                fingerprint,
                StatusCodes.Status402PaymentRequired,
                new { code = "authentication_failed", message = "The issuer challenge was not completed." });
        }

        authorization.State = "authorized";
        return Store(
            responses,
            operationKey,
            fingerprint,
            StatusCodes.Status200OK,
            new { state = "authorized", provider_reference = providerReference });
    }
});

app.MapPost("/v1/authorizations/{providerReference}/captures", (
    string providerReference,
    CaptureRequest request,
    HttpRequest httpRequest) =>
{
    var idempotencyKey = RequireIdempotencyKey(httpRequest);
    var operationKey = $"capture:{providerReference}:{idempotencyKey}";
    var fingerprint = Fingerprint(request);
    if (ReplayOrConflict(responses, operationKey, fingerprint) is { } replay)
    {
        return replay;
    }

    if (!authorizations.TryGetValue(providerReference, out var authorization))
    {
        return Results.NotFound(new { code = "authorization_not_found", message = "Authorization does not exist." });
    }

    if (request.PaymentId != authorization.PaymentId)
    {
        return Results.Conflict(new { code = "payment_reference_mismatch", message = "Capture payment id does not match the authorization." });
    }

    if (request.CaptureId == Guid.Empty)
    {
        return Results.UnprocessableEntity(new
        {
            code = "capture_id_invalid",
            message = "A stable capture id is required."
        });
    }

    lock (authorization)
    {
        if (ReplayOrConflict(responses, operationKey, fingerprint) is { } lockedReplay)
        {
            return lockedReplay;
        }

        var remaining = authorization.AuthorizedAmountMinor - authorization.CapturedAmountMinor - authorization.VoidedAmountMinor;
        if (authorization.State is not ("authorized" or "partially_captured") ||
            request.AmountMinor <= 0 || request.AmountMinor > remaining)
        {
            return Results.UnprocessableEntity(new
            {
                code = "capture_amount_invalid",
                message = $"Capture must be within the remaining {remaining} minor units."
            });
        }

        authorization.CapturedAmountMinor += request.AmountMinor;
        authorization.State = authorization.CapturedAmountMinor == authorization.AuthorizedAmountMinor
            ? "captured"
            : "partially_captured";
        return Store(
            responses,
            operationKey,
            fingerprint,
            StatusCodes.Status200OK,
            new { provider_reference = $"cap_{request.CaptureId:N}" });
    }
});

app.MapPost("/v1/authorizations/{providerReference}/voids", (
    string providerReference,
    VoidRequest request,
    HttpRequest httpRequest) =>
{
    var idempotencyKey = RequireIdempotencyKey(httpRequest);
    var operationKey = $"void:{providerReference}:{idempotencyKey}";
    var fingerprint = Fingerprint(request);
    if (ReplayOrConflict(responses, operationKey, fingerprint) is { } replay)
    {
        return replay;
    }

    if (!authorizations.TryGetValue(providerReference, out var authorization))
    {
        return Results.NotFound(new { code = "authorization_not_found", message = "Authorization does not exist." });
    }

    if (request.PaymentId != authorization.PaymentId)
    {
        return Results.Conflict(new { code = "payment_reference_mismatch", message = "Void payment id does not match the authorization." });
    }

    lock (authorization)
    {
        if (ReplayOrConflict(responses, operationKey, fingerprint) is { } lockedReplay)
        {
            return lockedReplay;
        }

        var remaining = authorization.AuthorizedAmountMinor - authorization.CapturedAmountMinor - authorization.VoidedAmountMinor;
        if (authorization.State is not ("authorized" or "partially_captured") ||
            request.AmountMinor != remaining || remaining <= 0)
        {
            return Results.UnprocessableEntity(new
            {
                code = "void_amount_invalid",
                message = $"Void amount must equal the remaining {remaining} minor units."
            });
        }

        authorization.VoidedAmountMinor += remaining;
        authorization.State = authorization.CapturedAmountMinor == 0 ? "voided" : "partially_captured_and_voided";
        return Store(
            responses,
            operationKey,
            fingerprint,
            StatusCodes.Status200OK,
            new { provider_reference = DeterministicReference("void", request.PaymentId, idempotencyKey) });
    }
});

app.MapPost("/v1/authorizations/{providerReference}/refunds", (
    string providerReference,
    RefundRequest request,
    HttpRequest httpRequest) =>
{
    var idempotencyKey = RequireIdempotencyKey(httpRequest);
    var operationKey = $"refund:{providerReference}:{idempotencyKey}";
    var fingerprint = Fingerprint(request);
    if (ReplayOrConflict(responses, operationKey, fingerprint) is { } replay)
    {
        return replay;
    }

    if (!authorizations.TryGetValue(providerReference, out var authorization))
    {
        return Results.NotFound(new { code = "authorization_not_found", message = "Authorization does not exist." });
    }

    if (request.PaymentId != authorization.PaymentId)
    {
        return Results.Conflict(new { code = "payment_reference_mismatch", message = "Refund payment id does not match the authorization." });
    }

    if (request.RefundId == Guid.Empty)
    {
        return Results.UnprocessableEntity(new
        {
            code = "refund_id_invalid",
            message = "A stable refund id is required."
        });
    }

    lock (authorization)
    {
        if (ReplayOrConflict(responses, operationKey, fingerprint) is { } lockedReplay)
        {
            return lockedReplay;
        }

        var refundable = authorization.CapturedAmountMinor - authorization.RefundedAmountMinor;
        if (authorization.State is not ("captured" or "partially_captured_and_voided" or "partially_refunded") ||
            request.AmountMinor <= 0 || request.AmountMinor > refundable)
        {
            return Results.UnprocessableEntity(new
            {
                code = "refund_amount_invalid",
                message = $"Refund must be within the remaining {refundable} minor units."
            });
        }

        authorization.RefundedAmountMinor += request.AmountMinor;
        authorization.State = authorization.RefundedAmountMinor == authorization.CapturedAmountMinor
            ? "refunded"
            : "partially_refunded";
        return Store(
            responses,
            operationKey,
            fingerprint,
            StatusCodes.Status200OK,
            new { provider_reference = $"ref_{request.RefundId:N}" });
    }
});

app.MapGet("/v1/authorizations/{providerReference}", (string providerReference) =>
{
    if (!authorizations.TryGetValue(providerReference, out var authorization))
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        state = authorization.State,
        captured_amount_minor = authorization.CapturedAmountMinor,
        error_code = authorization.State == "failed" ? "authentication_failed" : null,
        error_message = authorization.State == "failed" ? "The issuer challenge was not completed." : null
    });
});

await app.RunAsync();

static string RequireIdempotencyKey(HttpRequest request)
{
    var key = request.Headers["Idempotency-Key"].ToString().Trim();
    if (key.Length is < 8 or > 128)
    {
        throw new BadHttpRequestException("Idempotency-Key must contain 8 to 128 characters.");
    }

    return key;
}

static IResult Store(
    ConcurrentDictionary<string, StoredResponse> responses,
    string key,
    string requestSha256,
    int statusCode,
    object payload)
{
    var stored = responses.GetOrAdd(key, _ => new StoredResponse(requestSha256, statusCode, payload));
    return stored.ToResult();
}

static IResult? ReplayOrConflict(
    ConcurrentDictionary<string, StoredResponse> responses,
    string key,
    string requestSha256)
{
    if (!responses.TryGetValue(key, out var stored))
    {
        return null;
    }

    return stored.RequestSha256 == requestSha256
        ? stored.ToResult()
        : Results.Conflict(new
        {
            code = "idempotency_payload_mismatch",
            message = "The idempotency key was already used with a different provider request."
        });
}

static string Fingerprint<T>(T request) =>
    Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

static bool IsCurrency(string? value) =>
    value is { Length: 3 } && value.All(character => char.IsAsciiLetter(character));

static string DeterministicReference(string prefix, Guid paymentId, string idempotencyKey)
{
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{paymentId:N}:{idempotencyKey}"));
    return $"{prefix}_{Convert.ToHexString(digest)[..24].ToLowerInvariant()}";
}

public sealed record AuthorizationRequest(
    Guid PaymentId,
    long AmountMinor,
    string Currency,
    string PaymentMethodToken);

public sealed record AuthenticationRequest(
    Guid PaymentId,
    long AmountMinor,
    string AuthenticationResultToken);

public sealed record CaptureRequest(Guid PaymentId, Guid CaptureId, long AmountMinor);

public sealed record VoidRequest(Guid PaymentId, long AmountMinor);

public sealed record RefundRequest(Guid PaymentId, Guid RefundId, long AmountMinor);

public sealed record StoredResponse(string RequestSha256, int StatusCode, object Payload)
{
    public IResult ToResult() => Results.Json(Payload, statusCode: StatusCode);
}

public sealed class AuthorizationState
{
    public AuthorizationState(Guid paymentId, long authorizedAmountMinor, string currency, string state)
    {
        PaymentId = paymentId;
        AuthorizedAmountMinor = authorizedAmountMinor;
        Currency = currency;
        State = state;
    }

    public Guid PaymentId { get; }
    public long AuthorizedAmountMinor { get; }
    public string Currency { get; }
    public long CapturedAmountMinor { get; set; }
    public long RefundedAmountMinor { get; set; }
    public long VoidedAmountMinor { get; set; }
    public string State { get; set; }
}
