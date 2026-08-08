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
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
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

        using var response = await anonymousClient.GetAsync("/api/v1/providers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreatePayment_ReplaysSameRequestWithoutCreatingDuplicate()
    {
        var request = CreateRequest("order-idempotent-1");
        using var firstMessage = CreatePost("/api/v1/payments", request, "create-idempotent-0001");
        using var secondMessage = CreatePost("/api/v1/payments", request, "create-idempotent-0001");

        using var first = await Client.SendAsync(firstMessage, TestContext.Current.CancellationToken);
        using var second = await Client.SendAsync(secondMessage, TestContext.Current.CancellationToken);

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
        using var createResponse = await Client.SendAsync(createMessage, TestContext.Current.CancellationToken);
        var firstJson = await ReadJsonAsync(createResponse);
        var paymentId = firstJson.RootElement.GetProperty("id").GetGuid();

        using var secondClient = _factory?.CreateClient()
            ?? throw new InvalidOperationException("Test fixture is not initialized.");
        secondClient.DefaultRequestHeaders.Add("X-Api-Key", SecondMerchantApiKey);
        using var secondCreateMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-tenant-isolation-1"),
            sharedIdempotencyKey);
        using var secondCreateResponse = await secondClient.SendAsync(secondCreateMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, secondCreateResponse.StatusCode);
        var secondJson = await ReadJsonAsync(secondCreateResponse);
        Assert.NotEqual(paymentId, secondJson.RootElement.GetProperty("id").GetGuid());
        Assert.NotEqual(
            firstJson.RootElement.GetProperty("providerReference").GetString(),
            secondJson.RootElement.GetProperty("providerReference").GetString());

        using var response = await secondClient.GetAsync($"/api/v1/payments/{paymentId}", TestContext.Current.CancellationToken);

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

        using var first = await Client.SendAsync(firstMessage, TestContext.Current.CancellationToken);
        using var second = await Client.SendAsync(secondMessage, TestContext.Current.CancellationToken);

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
        using var createResponse = await Client.SendAsync(createMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var paymentId = (await ReadJsonAsync(createResponse)).RootElement.GetProperty("id").GetGuid();

        using var captureMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/capture",
            new { amountMinor = 12_990 },
            "capture-lifecycle-0001");
        using var captureResponse = await Client.SendAsync(captureMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, captureResponse.StatusCode);

        using var invalidRefundMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/refunds",
            new { amountMinor = 14_000 },
            "refund-ledger-invalid-0001");
        using var invalidRefundResponse = await Client.SendAsync(invalidRefundMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidRefundResponse.StatusCode);

        using var refundMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/refunds",
            new { amountMinor = 2_990 },
            "refund-lifecycle-0001");
        using var refundResponse = await Client.SendAsync(refundMessage, TestContext.Current.CancellationToken);
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

        using var response = await Client.SendAsync(message, TestContext.Current.CancellationToken);

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
        using var first = await Client.SendAsync(firstMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        using var retryMessage = CreatePost("/api/v1/payments", request, "create-transient-0001");
        using var retry = await Client.SendAsync(retryMessage, TestContext.Current.CancellationToken);

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
        using var createResponse = await Client.SendAsync(createMessage, TestContext.Current.CancellationToken);
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
        using var first = await Client.SendAsync(firstMessage, TestContext.Current.CancellationToken);
        using var second = await Client.SendAsync(secondMessage, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replay"));

        using var paymentResponse = await Client.GetAsync($"/api/v1/payments/{paymentId}", TestContext.Current.CancellationToken);
        var payment = await ReadJsonAsync(paymentResponse);
        Assert.Equal("Captured", payment.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SignedWebhook_RejectsExpiredSignature()
    {
        const string payload = "{\"id\":\"evt_expired\",\"type\":\"payment.captured\",\"providerReference\":\"missing\"}";
        using var message = CreateWebhookPost(payload, DateTimeOffset.UtcNow.AddMinutes(-10));

        using var response = await Client.SendAsync(message, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CaptureRefundAndSettlement_KeepLedgerBalanced()
    {
        using var createMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-ledger-1", amountMinor: 8_000),
            "create-ledger-0001");
        using var createResponse = await Client.SendAsync(createMessage, TestContext.Current.CancellationToken);
        var paymentId = (await ReadJsonAsync(createResponse)).RootElement.GetProperty("id").GetGuid();

        using var captureMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/capture",
            new { amountMinor = 8_000 },
            "capture-ledger-0001");
        using var captureResponse = await Client.SendAsync(captureMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, captureResponse.StatusCode);

        using var refundMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/refunds",
            new { amountMinor = 2_000 },
            "refund-ledger-0001");
        using var refundResponse = await Client.SendAsync(refundMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refundResponse.StatusCode);

        using var balancesBefore = await Client.GetAsync("/api/v1/ledger/balances?currency=EUR", TestContext.Current.CancellationToken);
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
        using var settlementResponse = await Client.SendAsync(settlementMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, settlementResponse.StatusCode);

        using var settlementReplayMessage = CreatePost(
            "/api/v1/settlements",
            settlementRequest,
            "settle-ledger-0001");
        using var settlementReplayResponse = await Client.SendAsync(settlementReplayMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, settlementReplayResponse.StatusCode);
        Assert.True(settlementReplayResponse.Headers.Contains("Idempotent-Replay"));

        using var balancesAfter = await Client.GetAsync("/api/v1/ledger/balances?currency=EUR", TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task ThreeDsChallenge_CanBeConfirmedIdempotently()
    {
        var request = new
        {
            merchantReference = "order-3ds-1",
            amountMinor = 15_000,
            currency = "EUR",
            provider = "mock-bank",
            paymentMethodToken = "tok_3ds_challenge"
        };
        using var createMessage = CreatePost("/api/v1/payments", request, "create-3ds-0001");
        using var createResponse = await Client.SendAsync(createMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadJsonAsync(createResponse);
        Assert.Equal("RequiresAction", created.RootElement.GetProperty("status").GetString());
        Assert.Equal("redirect", created.RootElement.GetProperty("nextAction").GetProperty("type").GetString());
        var paymentId = created.RootElement.GetProperty("id").GetGuid();

        using var confirmMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/confirm",
            new { authenticationResultToken = "auth_success" },
            "confirm-3ds-0001");
        using var confirmResponse = await Client.SendAsync(confirmMessage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        var confirmed = await ReadJsonAsync(confirmResponse);
        Assert.Equal("Authorized", confirmed.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, confirmed.RootElement.GetProperty("nextAction").ValueKind);

        using var replayMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/confirm",
            new { authenticationResultToken = "auth_success" },
            "confirm-3ds-0001");
        using var replayResponse = await Client.SendAsync(replayMessage, TestContext.Current.CancellationToken);
        Assert.True(replayResponse.Headers.Contains("Idempotent-Replay"));
    }

    [Fact]
    public async Task MultipleCapture_EnforcesRemainderAndSupportsVoid()
    {
        using var createMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-multi-capture-1", amountMinor: 10_000),
            "create-multi-capture-0001");
        using var createResponse = await Client.SendAsync(createMessage, TestContext.Current.CancellationToken);
        var paymentId = (await ReadJsonAsync(createResponse)).RootElement.GetProperty("id").GetGuid();

        using var firstCapture = CreatePost(
            $"/api/v1/payments/{paymentId}/capture",
            new { amountMinor = 4_000 },
            "capture-multi-0001");
        using var firstResponse = await Client.SendAsync(firstCapture, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var secondCapture = CreatePost(
            $"/api/v1/payments/{paymentId}/capture",
            new { amountMinor = 3_500 },
            "capture-multi-0002");
        using var secondResponse = await Client.SendAsync(secondCapture, TestContext.Current.CancellationToken);
        var partial = await ReadJsonAsync(secondResponse);
        Assert.Equal("PartiallyCaptured", partial.RootElement.GetProperty("status").GetString());
        Assert.Equal(2_500, partial.RootElement.GetProperty("remainingAuthorizedAmountMinor").GetInt64());
        Assert.Equal(2, partial.RootElement.GetProperty("captures").GetArrayLength());

        using var excessiveCapture = CreatePost(
            $"/api/v1/payments/{paymentId}/capture",
            new { amountMinor = 2_501 },
            "capture-multi-invalid-0001");
        using var excessiveResponse = await Client.SendAsync(excessiveCapture, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, excessiveResponse.StatusCode);

        using var voidMessage = CreatePost(
            $"/api/v1/payments/{paymentId}/void",
            body: null,
            "void-multi-0001");
        using var voidResponse = await Client.SendAsync(voidMessage, TestContext.Current.CancellationToken);
        var voided = await ReadJsonAsync(voidResponse);
        Assert.Equal("PartiallyCapturedAndVoided", voided.RootElement.GetProperty("status").GetString());
        Assert.Equal(2_500, voided.RootElement.GetProperty("voidedAmountMinor").GetInt64());
    }

    [Fact]
    public async Task ReconciliationImport_ClassifiesStateAndAmountDrift()
    {
        using var createMessage = CreatePost(
            "/api/v1/payments",
            CreateRequest("order-reconciliation-import-1", amountMinor: 6_000),
            "create-recon-import-0001");
        using var createResponse = await Client.SendAsync(createMessage, TestContext.Current.CancellationToken);
        var created = await ReadJsonAsync(createResponse);
        var providerReference = created.RootElement.GetProperty("providerReference").GetString();
        var periodStart = DateTimeOffset.UtcNow.AddHours(-1);
        var periodEnd = DateTimeOffset.UtcNow.AddHours(1);
        var csv = $"provider_reference,authorized_amount_minor,captured_amount_minor,currency,state,occurred_at\n" +
                  $"{providerReference},6000,1000,EUR,captured,{DateTimeOffset.UtcNow:O}\n";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/reconciliation/imports/mock-bank?periodStart={Uri.EscapeDataString(periodStart.ToString("O"))}&periodEnd={Uri.EscapeDataString(periodEnd.ToString("O"))}")
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv")
        };
        request.Headers.Add("X-Report-Name", "mock-bank-20260808.csv");

        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var report = await ReadJsonAsync(response);
        Assert.Equal("ReviewRequired", report.RootElement.GetProperty("status").GetString());
        var types = report.RootElement.GetProperty("issues")
            .EnumerateArray()
            .Select(issue => issue.GetProperty("type").GetString())
            .ToArray();
        Assert.Contains("CapturedAmountMismatch", types);
        Assert.Contains("StateMismatch", types);
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
        var timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SentinelPayApiFactory.WebhookSigningMaterial),
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
        using var response = await Client.GetAsync("/api/v1/ledger/journals?limit=100", TestContext.Current.CancellationToken);
        var json = await ReadJsonAsync(response);
        return json.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private async Task SeedSecondMerchantAsync()
    {
        var factory = _factory ?? throw new InvalidOperationException("Test fixture is not initialized.");
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
        await dbContext.Merchants.AddAsync(
            Merchant.Create(SecondMerchantId, "Second Merchant", DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        await dbContext.ApiKeyCredentials.AddAsync(new ApiKeyCredential
        {
            Id = Guid.NewGuid(),
            MerchantId = SecondMerchantId,
            Name = "integration-test-key",
            KeyHash = ApiKeyHasher.Hash(SecondMerchantApiKey),
            Scopes = "payments:read payments:write ledger:read settlements:read settlements:write reconciliation:write",
            CreatedAt = DateTimeOffset.UtcNow
        }, TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
