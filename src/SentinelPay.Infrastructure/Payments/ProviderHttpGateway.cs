using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Payments;

public sealed class ProviderHttpGateway : IPaymentGateway
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumAttempts = 3;
    private readonly HttpClient _httpClient;
    private readonly ProviderCircuitBreaker _circuitBreaker;

    public ProviderHttpGateway(HttpClient httpClient, ProviderCircuitBreaker circuitBreaker)
    {
        _httpClient = httpClient;
        _circuitBreaker = circuitBreaker;
    }

    public string Name => "acquirer-http";

    public async Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Post,
            "v1/authorizations",
            new
            {
                payment_id = request.PaymentId,
                amount_minor = request.AmountMinor,
                currency = request.Currency,
                payment_method_token = request.PaymentMethodToken
            },
            request.IdempotencyKey,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return new GatewayAuthorizationResult(
                GatewayAuthorizationState.Declined,
                null,
                null,
                error.Code,
                error.Message);
        }

        var body = await ReadRequiredAsync<AuthorizationResponse>(response, cancellationToken);
        return MapAuthorization(body);
    }

    public async Task<GatewayAuthorizationResult> CompleteAuthenticationAsync(
        GatewayAuthenticationRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Post,
            $"v1/authorizations/{Uri.EscapeDataString(request.ProviderReference)}/authenticate",
            new
            {
                payment_id = request.PaymentId,
                amount_minor = request.AmountMinor,
                authentication_result_token = request.AuthenticationResultToken
            },
            request.IdempotencyKey,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return new GatewayAuthorizationResult(
                GatewayAuthorizationState.Declined,
                request.ProviderReference,
                null,
                error.Code,
                error.Message);
        }

        return MapAuthorization(await ReadRequiredAsync<AuthorizationResponse>(response, cancellationToken));
    }

    public async Task<GatewayOperationResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Post,
            $"v1/authorizations/{Uri.EscapeDataString(request.ProviderReference)}/captures",
            new
            {
                payment_id = request.PaymentId,
                capture_id = request.CaptureId,
                amount_minor = request.AmountMinor
            },
            request.IdempotencyKey,
            cancellationToken);
        return await MapOperationAsync(response, cancellationToken);
    }

    public async Task<GatewayOperationResult> VoidAsync(
        GatewayVoidRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Post,
            $"v1/authorizations/{Uri.EscapeDataString(request.ProviderReference)}/voids",
            new { payment_id = request.PaymentId, amount_minor = request.AmountMinor },
            request.IdempotencyKey,
            cancellationToken);
        return await MapOperationAsync(response, cancellationToken);
    }

    public async Task<GatewayRefundResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Post,
            $"v1/authorizations/{Uri.EscapeDataString(request.ProviderReference)}/refunds",
            new
            {
                payment_id = request.PaymentId,
                refund_id = request.RefundId,
                amount_minor = request.AmountMinor
            },
            request.IdempotencyKey,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return new GatewayRefundResult(false, null, error.Code, error.Message);
        }

        var body = await ReadRequiredAsync<OperationResponse>(response, cancellationToken);
        return new GatewayRefundResult(true, body.ProviderReference, null, null);
    }

    public async Task<GatewayPaymentStatusResult> GetStatusAsync(
        string providerReference,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"v1/authorizations/{Uri.EscapeDataString(providerReference)}"),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GatewayPaymentStatusResult(GatewayPaymentState.Unknown, null, null, null);
        }

        response.EnsureSuccessStatusCode();
        var body = await ReadRequiredAsync<StatusResponse>(response, cancellationToken);
        return new GatewayPaymentStatusResult(
            ParsePaymentState(body.State),
            body.CapturedAmountMinor,
            body.ErrorCode,
            body.ErrorMessage);
    }

    private async Task<HttpResponseMessage> SendMutationAsync<T>(
        HttpMethod method,
        string path,
        T payload,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await SendWithRetryAsync(
            () =>
            {
                var message = new HttpRequestMessage(method, path)
                {
                    Content = JsonContent.Create(payload, options: SerializerOptions)
                };
                message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                message.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString("N"));
                return message;
            },
            cancellationToken);

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken) =>
        await _circuitBreaker.ExecuteAsync(
            () => SendWithRetryCoreAsync(requestFactory, cancellationToken),
            cancellationToken);

    private async Task<HttpResponseMessage> SendWithRetryCoreAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == MaximumAttempts)
                {
                    if (IsTransient(response.StatusCode))
                    {
                        var statusCode = response.StatusCode;
                        response.Dispose();
                        throw new HttpRequestException(
                            $"Provider remained unavailable after {MaximumAttempts} attempts.",
                            null,
                            statusCode);
                    }

                    return response;
                }

                var delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < MaximumAttempts)
            {
                lastException = exception;
                await Task.Delay(Backoff(attempt), cancellationToken);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested && attempt < MaximumAttempts)
            {
                lastException = exception;
                await Task.Delay(Backoff(attempt), cancellationToken);
            }
        }

        throw new HttpRequestException("Provider request failed after bounded retries.", lastException);
    }

    private static async Task<GatewayOperationResult> MapOperationAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return new GatewayOperationResult(false, null, error.Code, error.Message);
        }

        var body = await ReadRequiredAsync<OperationResponse>(response, cancellationToken);
        return new GatewayOperationResult(true, body.ProviderReference, null, null);
    }

    private static GatewayAuthorizationResult MapAuthorization(AuthorizationResponse body)
    {
        var state = body.State.ToLowerInvariant() switch
        {
            "authorized" => GatewayAuthorizationState.Authorized,
            "requires_action" => GatewayAuthorizationState.RequiresAction,
            "declined" => GatewayAuthorizationState.Declined,
            _ => throw new HttpRequestException($"Provider returned unsupported authorization state '{body.State}'.")
        };
        GatewayNextAction? nextAction = null;
        if (state == GatewayAuthorizationState.RequiresAction)
        {
            if (body.NextAction is null)
            {
                throw new HttpRequestException("Provider omitted next_action for a required authentication.");
            }

            nextAction = new GatewayNextAction(
                body.NextAction.Type,
                body.NextAction.Url,
                body.NextAction.ExpiresAt);
        }

        return new GatewayAuthorizationResult(
            state,
            body.ProviderReference,
            nextAction,
            body.ErrorCode,
            body.ErrorMessage);
    }

    private static GatewayPaymentState ParsePaymentState(string state) => state.ToLowerInvariant() switch
    {
        "requires_action" => GatewayPaymentState.RequiresAction,
        "authorized" => GatewayPaymentState.Authorized,
        "partially_captured" => GatewayPaymentState.PartiallyCaptured,
        "captured" => GatewayPaymentState.Captured,
        "partially_captured_and_voided" => GatewayPaymentState.PartiallyCapturedAndVoided,
        "voided" => GatewayPaymentState.Voided,
        "refunded" or "partially_refunded" => GatewayPaymentState.Refunded,
        "failed" => GatewayPaymentState.Failed,
        _ => GatewayPaymentState.Unknown
    };

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt) =>
        response.Headers.RetryAfter?.Delta is { } retryAfter && retryAfter <= TimeSpan.FromSeconds(2)
            ? retryAfter
            : Backoff(attempt);

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(1000, 75 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(10, 50));

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken) where T : class =>
        await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken)
            ?? throw new HttpRequestException("Provider returned an empty JSON response.");

    private static async Task<ProviderErrorResponse> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProviderErrorResponse>(SerializerOptions, cancellationToken)
                ?? new ProviderErrorResponse("provider_error", $"Provider returned HTTP {(int)response.StatusCode}.");
        }
        catch (JsonException)
        {
            return new ProviderErrorResponse("invalid_provider_response", $"Provider returned HTTP {(int)response.StatusCode} with an invalid error body.");
        }
    }

    private sealed record AuthorizationResponse(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("provider_reference")] string? ProviderReference,
        [property: JsonPropertyName("next_action")] NextActionResponse? NextAction,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("error_message")] string? ErrorMessage);

    private sealed record NextActionResponse(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    private sealed record OperationResponse(
        [property: JsonPropertyName("provider_reference")] string ProviderReference);

    private sealed record StatusResponse(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("captured_amount_minor")] long? CapturedAmountMinor,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("error_message")] string? ErrorMessage);

    private sealed record ProviderErrorResponse(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
