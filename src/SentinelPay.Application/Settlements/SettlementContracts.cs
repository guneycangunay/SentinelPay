using SentinelPay.Domain.Settlements;

namespace SentinelPay.Application.Settlements;

public sealed record CreateSettlementCommand(
    Guid MerchantId,
    string Currency,
    DateTimeOffset PeriodEnd,
    string IdempotencyKey);

public sealed record SettlementResult(SettlementResponse Settlement, bool IsReplay);

public sealed record SettlementResponse(
    Guid Id,
    string Currency,
    long AmountMinor,
    DateTimeOffset PeriodEnd,
    SettlementStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt)
{
    public static SettlementResponse From(SettlementBatch settlement) => new(
        settlement.Id,
        settlement.Currency,
        settlement.AmountMinor,
        settlement.PeriodEnd,
        settlement.Status,
        settlement.CreatedAt,
        settlement.PaidAt);
}

public sealed class NoPayableBalanceException : Exception
{
    public NoPayableBalanceException(string currency)
        : base($"No positive unsettled merchant payable balance exists for {currency}.")
    {
    }
}
