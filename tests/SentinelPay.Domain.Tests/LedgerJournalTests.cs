using SentinelPay.Domain;
using SentinelPay.Domain.Ledger;

namespace SentinelPay.Domain.Tests;

public sealed class LedgerJournalTests
{
    private static readonly Guid MerchantId = Guid.Parse("2dc5f437-0a11-4c67-a810-b3e784470f73");
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_AcceptsBalancedDoubleEntryJournal()
    {
        var journal = LedgerJournal.Create(
            MerchantId,
            Guid.NewGuid(),
            "capture:payment-1",
            "eur",
            "Capture",
            [
                new LedgerLineDraft(LedgerAccount.ProviderClearing, LedgerDirection.Debit, 12_990),
                new LedgerLineDraft(LedgerAccount.MerchantPayable, LedgerDirection.Credit, 12_990)
            ],
            Now);

        Assert.Equal("EUR", journal.Currency);
        Assert.Equal(2, journal.Lines.Count);
        Assert.Equal(
            journal.Lines.Where(line => line.Direction == LedgerDirection.Debit).Sum(line => line.AmountMinor),
            journal.Lines.Where(line => line.Direction == LedgerDirection.Credit).Sum(line => line.AmountMinor));
    }

    [Fact]
    public void Create_RejectsUnbalancedJournal()
    {
        var exception = Assert.Throws<DomainException>(() => LedgerJournal.Create(
            MerchantId,
            null,
            "broken:journal-1",
            "EUR",
            "Broken journal",
            [
                new LedgerLineDraft(LedgerAccount.ProviderClearing, LedgerDirection.Debit, 10_00),
                new LedgerLineDraft(LedgerAccount.MerchantPayable, LedgerDirection.Credit, 9_00)
            ],
            Now));

        Assert.Contains("unbalanced", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LedgerLine_CannotMoveBetweenSettlementBatches()
    {
        var journal = LedgerJournal.Create(
            MerchantId,
            null,
            "settlement-source:1",
            "EUR",
            "Settlement source",
            [
                new LedgerLineDraft(LedgerAccount.MerchantPayable, LedgerDirection.Debit, 5_00),
                new LedgerLineDraft(LedgerAccount.SettlementClearing, LedgerDirection.Credit, 5_00)
            ],
            Now);
        var line = journal.Lines.First();
        var firstBatch = Guid.NewGuid();
        line.AssignToSettlement(firstBatch);

        Assert.Throws<DomainException>(() => line.AssignToSettlement(Guid.NewGuid()));
        Assert.Equal(firstBatch, line.SettlementBatchId);
    }
}
