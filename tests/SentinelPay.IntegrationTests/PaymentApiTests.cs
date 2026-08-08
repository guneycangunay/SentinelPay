using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentinelPay.Domain.Merchants;
using SentinelPay.Infrastructure.Persistence;
using SentinelPay.Infrastructure.Security;
using Testcontainers.PostgreSql;

namespace SentinelPay.IntegrationTests;

public sealed class PaymentApiTests : IAsyncLifetime
{
    private const string SecondMerchantApiKey = "sp_test_second_merchant_f3a91c";
    private static readonly Guid SecondMerchantId = Guid.Parse("75aa29da-7fbb-4ac6-8e1f-ad4aeeea9822");
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
        _client.DefaultRequestHeaders.Add("X-Api-Key", SentinelPayApiFactory.DevelopmentApiKey);
        await SeedSecondMerchantAsync();
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
    public async Task ProtectedEndpoint_RejectsMissingMerchantCredential()
    {
        using var anonymousClient = _factory?.CreateClient()
            ?? throw new InvalidOperationException("Test fixture is not initialized.");

        using var response = await anonymousClient.GetAsync("/api/v1/providers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
    public async Task Merchant_CannotReadAnotherMerchantsPayment()
    {
        const string sharedIdempotencyKey = "create-tenant-isolation-0001";
        using var createMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-tenant-isolation-1"),
            sharedIdempotencyKey);
        using var createResponse = await Client.SendAsync(createMessage);
        var firstJson = await ReadJsonAsync(createResponse);
        var paymentId = firstJson.RootElement.GetProperty("id").GetGuid();

        using var secondClient = _factory?.CreateClient()
            ?? throw new InvalidOperationException("Test fixture is not initialized.");
        secondClient.DefaultRequestHeaders.Add("X-Api-Key", SecondMerchantApiKey);
        using var secondCreateMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-tenant-isolation-1"),
            sharedIdempotencyKey);
        using var secondCreateResponse = await secondClient.SendAsync(secondCreateMessage);
        Assert.Equal(HttpStatusCode.Created, secondCreateResponse.StatusCode);
        var secondJson = await ReadJsonAsync(secondCreateResponse);
        Assert.NotEqual(paymentId, secondJson.RootElement.GetProperty("id").GetGuid());
        Assert.NotEqual(
            firstJson.RootElement.GetProperty("providerReference").GetString(),
            secondJson.RootElement.GetProperty("providerReference").GetString());

        using var response = await secondClient.GetAsync($"/api/v1/payments/{paymentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        using var invalidRefundMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/refunds",
            new { amountMinor = 9_000 },
            "refund-ledger-invalid-0001");
        using var invalidRefundResponse = await Client.SendAsync(invalidRefundMessage);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidRefundResponse.StatusCode);

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
    public async Task TransientProviderFailure_ResumesPersistedOperationWithSameKey()
    {
        var request = new
        {
            merchantReference = "order-transient-1",
            amountMinor = 7_500,
            currency = "EUR",
            provider = "mock-bank",
            paymentMethodToken = "tok_transient_once"
        };
        using var firstMessage = CreatePost("/api/v1/payments", request, "create-transient-0001");
        using var first = await Client.SendAsync(firstMessage);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        using var retryMessage = CreatePost("/api/v1/payments", request, "create-transient-0001");
        using var retry = await Client.SendAsync(retryMessage);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var json = await ReadJsonAsync(retry);
        Assert.Equal("Authorized", json.RootElement.GetProperty("status").GetString());
        var operation = json.RootElement.GetProperty("operations").EnumerateArray().Single();
        Assert.Equal("Succeeded", operation.GetProperty("status").GetString());
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

    [Fact]
    public async Task SignedWebhook_RejectsExpiredSignature()
    {
        const string payload = "{\"id\":\"evt_expired\",\"type\":\"payment.captured\",\"providerReference\":\"missing\"}";
        using var message = CreateWebhookPost(payload, DateTimeOffset.UtcNow.AddMinutes(-10));

        using var response = await Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CaptureRefundAndSettlement_KeepLedgerBalanced()
    {
        using var createMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-ledger-1", amountMinor: 8_000),
            "create-ledger-0001");
        using var createResponse = await Client.SendAsync(createMessage);
        var paymentId = (await ReadJsonAsync(createResponse)).RootElement.GetProperty("id").GetGuid();

        using var captureMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/capture",
            body: null,
            "capture-ledger-0001");
        using var captureResponse = await Client.SendAsync(captureMessage);
        Assert.Equal(HttpStatusCode.OK, captureResponse.StatusCode);

        using var refundMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/refunds",
            new { amountMinor = 2_000 },
            "refund-ledger-0001");
        using var refundResponse = await Client.SendAsync(refundMessage);
        Assert.Equal(HttpStatusCode.OK, refundResponse.StatusCode);

        using var balancesBefore = await Client.GetAsync("/api/v1/ledger/balances?currency=EUR");
        var before = await ReadJsonAsync(balancesBefore);
        var payableBefore = before.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("account").GetString() == "MerchantPayable")
            .GetProperty("netMinor")
            .GetInt64();
        Assert.True(payableBefore >= 6_000);

        var settlementRequest = new { currency = "EUR", periodEnd = DateTimeOffset.UtcNow };
        using var settlementMessage = CreatePost(
            "/api/v1/settlements",
            settlementRequest,
            "settle-ledger-0001");
        using var settlementResponse = await Client.SendAsync(settlementMessage);
        Assert.Equal(HttpStatusCode.Created, settlementResponse.StatusCode);

        using var settlementReplayMessage = CreatePost(
            "/api/v1/settlements",
            settlementRequest,
            "settle-ledger-0001");
        using var settlementReplayResponse = await Client.SendAsync(settlementReplayMessage);
        Assert.Equal(HttpStatusCode.OK, settlementReplayResponse.StatusCode);
        Assert.True(settlementReplayResponse.Headers.Contains("Idempotent-Replay"));

        using var balancesAfter = await Client.GetAsync("/api/v1/ledger/balances?currency=EUR");
        var after = await ReadJsonAsync(balancesAfter);
        var payableAfter = after.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("account").GetString() == "MerchantPayable")
            .GetProperty("netMinor")
            .GetInt64();
        Assert.Equal(0, payableAfter);

        foreach (var journal in await GetJournalsAsync())
        {
            Assert.False(string.IsNullOrWhiteSpace(journal.GetProperty("externalReference").GetString()));
        }
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

    private static HttpRequestMessage CreateWebhookPost(string payload, DateTimeOffset? signedAt = null)
    {
        const string secret = "${SENTINELPAY_TEST_SIGNING_MATERIAL}";
        var timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/mock-bank")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("X-SentinelPay-Signature", $"t={timestamp},v1={signature}");
        return message;
    }

    private async Task<JsonElement[]> GetJournalsAsync()
    {
        using var response = await Client.GetAsync("/api/v1/ledger/journals?limit=100");
        var json = await ReadJsonAsync(response);
        return json.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private async Task SeedSecondMerchantAsync()
    {
        var factory = _factory ?? throw new InvalidOperationException("Test fixture is not initialized.");
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
        await dbContext.Merchants.AddAsync(
            Merchant.Create(SecondMerchantId, "Second Merchant", DateTimeOffset.UtcNow));
        await dbContext.ApiKeyCredentials.AddAsync(new ApiKeyCredential
        {
            Id = Guid.NewGuid(),
            MerchantId = SecondMerchantId,
            Name = "integration-test-key",
            KeyHash = ApiKeyHasher.Hash(SecondMerchantApiKey),
            Scopes = "payments:read payments:write ledger:read settlements:read settlements:write",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
