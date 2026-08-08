using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace SentinelPay.IntegrationTests;

public sealed class PaymentApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("sentinelpay_tests")
        .WithUsername("sentinelpay")
        .WithPassword("sentinelpay_tests")
        .Build();
    private SentinelPayApiFactory? _factory;
    private HttpClient? _client;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new SentinelPayApiFactory(_postgres.GetConnectionString());
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task CreatePayment_ReplaysSameRequestWithoutCreatingDuplicate()
    {
        var request = CreateRequest("order-idempotent-1");
        using var firstMessage = CreatePost("/api/v1/payments", request, "create-idempotent-0001");
        using var secondMessage = CreatePost("/api/v1/payments", request, "create-idempotent-0001");

        using var first = await Client.SendAsync(firstMessage);
        using var second = await Client.SendAsync(secondMessage);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.TryGetValues("Idempotent-Replay", out var values));
        Assert.Contains("true", values);

        var firstId = (await ReadJsonAsync(first)).RootElement.GetProperty("id").GetGuid();
        var secondId = (await ReadJsonAsync(second)).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task CreatePayment_RejectsReusedKeyWithDifferentPayload()
    {
        using var firstMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-conflict", amountMinor: 10_00),
            "create-conflict-0001");
        using var secondMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-conflict", amountMinor: 20_00),
            "create-conflict-0001");

        using var first = await Client.SendAsync(firstMessage);
        using var second = await Client.SendAsync(secondMessage);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Payment_CanBeAuthorizedCapturedAndPartiallyRefunded()
    {
        using var createMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-lifecycle-1", amountMinor: 12_990),
            "create-lifecycle-0001");
        using var createResponse = await Client.SendAsync(createMessage);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var paymentId = (await ReadJsonAsync(createResponse)).RootElement.GetProperty("id").GetGuid();

        using var captureMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/capture",
            body: null,
            "capture-lifecycle-0001");
        using var captureResponse = await Client.SendAsync(captureMessage);
        Assert.Equal(HttpStatusCode.OK, captureResponse.StatusCode);

        using var refundMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/refunds",
            new { amountMinor = 2_990 },
            "refund-lifecycle-0001");
        using var refundResponse = await Client.SendAsync(refundMessage);
        Assert.Equal(HttpStatusCode.OK, refundResponse.StatusCode);
        var refundJson = await ReadJsonAsync(refundResponse);
        Assert.Equal("PartiallyRefunded", refundJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(2_990, refundJson.RootElement.GetProperty("refundedAmountMinor").GetInt64());
    }

    [Fact]
    public async Task DeclinedToken_PersistsFailedPaymentForAuditability()
    {
        var request = new
        {
            merchantReference = "order-declined-1",
            amountMinor = 5_000,
            currency = "EUR",
            provider = "mock-bank",
            paymentMethodToken = "tok_declined"
        };
        using var message = CreatePost("/api/v1/payments", request, "create-declined-0001");

        using var response = await Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("Failed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("card_declined", json.RootElement.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task SignedWebhook_IsProcessedOnceAndReplayedSafely()
    {
        using var createMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-webhook-1"),
            "create-webhook-0001");
        using var createResponse = await Client.SendAsync(createMessage);
        var created = await ReadJsonAsync(createResponse);
        var paymentId = created.RootElement.GetProperty("id").GetGuid();
        var providerReference = created.RootElement.GetProperty("providerReference").GetString();
        var payload = JsonSerializer.Serialize(new
        {
            id = "evt_webhook_0001",
            type = "payment.captured",
            providerReference
        });

        using var firstMessage = CreateWebhookPost(payload);
        using var secondMessage = CreateWebhookPost(payload);
        using var first = await Client.SendAsync(firstMessage);
        using var second = await Client.SendAsync(secondMessage);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replay"));

        using var paymentResponse = await Client.GetAsync($"/api/v1/payments/{paymentId}");
        var payment = await ReadJsonAsync(paymentResponse);
        Assert.Equal("Captured", payment.RootElement.GetProperty("status").GetString());
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("Test fixture is not initialized.");

    private static object CreateRequest(string merchantReference, long amountMinor = 12_990) => new
    {
        merchantReference,
        amountMinor,
        currency = "EUR",
        provider = "mock-bank",
        paymentMethodToken = "tok_visa"
    };

    private static HttpRequestMessage CreatePost(string path, object? body, string idempotencyKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        return message;
    }

    private static HttpRequestMessage CreateWebhookPost(string payload)
    {
        const string secret = "${SENTINELPAY_TEST_SIGNING_MATERIAL}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload)));
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/mock-bank")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("X-SentinelPay-Signature", signature);
        return message;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
