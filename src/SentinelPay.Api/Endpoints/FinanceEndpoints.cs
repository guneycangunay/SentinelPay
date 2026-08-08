using SentinelPay.Api.Security;
using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Payments;
using SentinelPay.Application.Settlements;

namespace SentinelPay.Api.Endpoints;

public static class FinanceEndpoints
{
    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/ledger/balances", GetBalancesAsync)
            .WithTags("Ledger")
            .WithName("GetLedgerBalances")
            .RequireAuthorization(SentinelPayPolicies.LedgerRead);

        endpoints.MapGet("/api/v1/ledger/journals", GetRecentJournalsAsync)
            .WithTags("Ledger")
            .WithName("GetLedgerJournals")
            .RequireAuthorization(SentinelPayPolicies.LedgerRead);

        endpoints.MapPost("/api/v1/settlements", CreateSettlementAsync)
            .WithTags("Settlements")
            .WithName("CreateSettlement")
            .Produces<SettlementResponse>(StatusCodes.Status201Created)
            .RequireAuthorization(SentinelPayPolicies.SettlementsWrite);

        endpoints.MapGet("/api/v1/settlements/{settlementId:guid}", GetSettlementAsync)
            .WithTags("Settlements")
            .WithName("GetSettlement")
            .Produces<SettlementResponse>()
            .RequireAuthorization(SentinelPayPolicies.SettlementsRead);

        return endpoints;
    }

    private static async Task<IResult> GetBalancesAsync(
        string currency,
        HttpContext context,
        ILedgerReader ledger,
        CancellationToken cancellationToken) =>
        Results.Ok(await ledger.GetBalancesAsync(
            context.GetMerchantId(),
            currency,
            cancellationToken));

    private static async Task<IResult> GetRecentJournalsAsync(
        HttpContext context,
        ILedgerReader ledger,
        CancellationToken cancellationToken,
        int limit = 25) =>
        Results.Ok(await ledger.GetRecentJournalsAsync(
            context.GetMerchantId(),
            limit,
            cancellationToken));

    private static async Task<IResult> CreateSettlementAsync(
        CreateSettlementRequest request,
        HttpContext context,
        SettlementService settlementService,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new IdempotencyConflictException("The Idempotency-Key header is required.");
        }

        var result = await settlementService.CreateAsync(
            new CreateSettlementCommand(
                context.GetMerchantId(),
                request.Currency,
                request.PeriodEnd,
                idempotencyKey.Trim()),
            cancellationToken);
        if (result.IsReplay)
        {
            context.Response.Headers["Idempotent-Replay"] = "true";
            return Results.Ok(result.Settlement);
        }

        return Results.Created($"/api/v1/settlements/{result.Settlement.Id}", result.Settlement);
    }

    private static async Task<IResult> GetSettlementAsync(
        Guid settlementId,
        HttpContext context,
        SettlementService settlementService,
        CancellationToken cancellationToken) =>
        Results.Ok(await settlementService.GetAsync(
            context.GetMerchantId(),
            settlementId,
            cancellationToken));
}

public sealed record CreateSettlementRequest(string Currency, DateTimeOffset PeriodEnd);
