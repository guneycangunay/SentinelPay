using SentinelPay.Domain;
using SentinelPay.Domain.Settlements;

namespace SentinelPay.Domain.Tests;

public sealed class SettlementBatchTests
{
    private static readonly Guid MerchantId = Guid.Parse("2dc5f437-0a11-4c67-a810-b3e784470f73");
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_NormalizesCurrencyAndStartsPending()
    {
        var settlement = SettlementBatch.Create(
            MerchantId,
            "eur",
            10_000,
            "settlement-key-0001",
            Now,
            Now);

        Assert.Equal("EUR", settlement.Currency);
        Assert.Equal(SettlementStatus.Pending, settlement.Status);
        Assert.Equal(10_000, settlement.AmountMinor);
    }

    [Fact]
    public void Create_RejectsNonPositiveBalance()
    {
        Assert.Throws<DomainException>(() => SettlementBatch.Create(
            MerchantId,
            "EUR",
            0,
            "settlement-key-0002",
            Now,
            Now));
    }

    [Fact]
    public void MarkPaid_IsForwardOnly()
    {
        var settlement = SettlementBatch.Create(
            MerchantId,
            "EUR",
            10_000,
            "settlement-key-0003",
            Now,
            Now);
        settlement.MarkPaid(Now.AddMinutes(1));

        Assert.Equal(SettlementStatus.Paid, settlement.Status);
        Assert.Throws<DomainException>(() => settlement.MarkPaid(Now.AddMinutes(2)));
    }
}
