using System.Net;
using System.Text;
using SentinelPay.Application.Abstractions;
using SentinelPay.Infrastructure;
using SentinelPay.Infrastructure.Payments;

namespace SentinelPay.IntegrationTests;

public sealed class ProviderHttpGatewayContractTests
{
    [Fact]
    public async Task Authorization_RetriesRateLimitWithSameProviderIdempotencyKey()
    {
        var attempts = new List<string>();
        var handler = new StubHttpMessageHandler(async (request, attempt, cancellationToken) =>
        {
            attempts.Add(request.Headers.GetValues("Idempotency-Key").Single());
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (attempt == 1)
            {
                var limited = JsonResponse(
                    HttpStatusCode.TooManyRequests,
                    """{"code":"rate_limited","message":"retry"}""");
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }

            return JsonResponse(
                HttpStatusCode.OK,
                """{"state":"authorized","provider_reference":"auth_http_123"}""");
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/") };
        var gateway = CreateGateway(client);

        var result = await gateway.AuthorizeAsync(
            new GatewayAuthorizationRequest(
                Guid.NewGuid(),
                12_990,
                "EUR",
                "tok_http_rate_limited",
                "provider-contract-0001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(GatewayAuthorizationState.Authorized, result.State);
        Assert.Equal("auth_http_123", result.ProviderReference);
        Assert.Equal(2, attempts.Count);
        Assert.Single(attempts.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Authorization_MapsThreeDsNextActionWithoutExposingPaymentToken()
    {
        string? outboundBody = null;
        var handler = new StubHttpMessageHandler(async (request, _, cancellationToken) =>
        {
            outboundBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                HttpStatusCode.OK,
                """
                {
                  "state":"requires_action",
                  "provider_reference":"auth_http_3ds",
                  "next_action":{
                    "type":"redirect",
                    "url":"https://issuer.test/challenge/session-1",
                    "expires_at":"2026-08-08T20:00:00Z"
                  }
                }
                """);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/") };
        var gateway = CreateGateway(client);

        var result = await gateway.AuthorizeAsync(
            new GatewayAuthorizationRequest(
                Guid.NewGuid(),
                5_000,
                "EUR",
                "tok_provider_only_abc",
                "provider-contract-3ds-0001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(GatewayAuthorizationState.RequiresAction, result.State);
        Assert.Equal("redirect", result.NextAction?.Type);
        var serialized = outboundBody ?? throw new InvalidOperationException("No provider request was recorded.");
        Assert.Contains("payment_method_token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("card_number", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cvv", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_MapsProviderBusinessFailureWithoutBlindApplicationRetry()
    {
        var handler = new StubHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.UnprocessableEntity,
            """{"code":"capture_amount_invalid","message":"capture exceeds authorization"}""")));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/") };
        var gateway = CreateGateway(client);

        var result = await gateway.CaptureAsync(
            new GatewayCaptureRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "auth_http_123",
                8_000,
                "provider-capture-0001"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal("capture_amount_invalid", result.ErrorCode);
        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task RepeatedTransientFailure_OpensSharedProviderCircuit()
    {
        var handler = new StubHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """{"code":"provider_unavailable","message":"maintenance"}""")));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/") };
        var gateway = CreateGateway(client);
        var request = new GatewayAuthorizationRequest(
            Guid.NewGuid(),
            1_000,
            "EUR",
            "tok_http_timeout",
            "provider-circuit-0001");

        for (var failure = 0; failure < 5; failure++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                gateway.AuthorizeAsync(request, TestContext.Current.CancellationToken));
        }

        var attemptsBeforeOpenCall = handler.AttemptCount;
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.AuthorizeAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("circuit is open", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(attemptsBeforeOpenCall, handler.AttemptCount);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static ProviderHttpGateway CreateGateway(HttpClient client) =>
        new(client, new ProviderCircuitBreaker(new SystemClock()));

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public int AttemptCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            return _handler(request, AttemptCount, cancellationToken);
        }
    }
}
