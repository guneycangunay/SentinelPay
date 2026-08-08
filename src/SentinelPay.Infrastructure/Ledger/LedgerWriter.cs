using Microsoft.EntityFrameworkCore;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Ledger;
using SentinelPay.Domain.Payments;
using SentinelPay.Domain.Settlements;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Ledger;

public sealed class LedgerWriter : ILedgerWriter
{
    private readonly SentinelPayDbContext _dbContext;

    public LedgerWriter(SentinelPayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordCaptureAsync(
        Payment payment,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var externalReference = $"capture:{payment.Id:N}";
        if (await ExistsAsync(externalReference, cancellationToken))
        {
            return;
        }

        var journal = LedgerJournal.Create(
            payment.MerchantId,
            payment.Id,
            externalReference,
            payment.Currency,
            "Capture funds into merchant payable",
            [
                new LedgerLineDraft(LedgerAccount.ProviderClearing, LedgerDirection.Debit, payment.CapturedAmountMinor),
                new LedgerLineDraft(LedgerAccount.MerchantPayable, LedgerDirection.Credit, payment.CapturedAmountMinor)
            ],
            now);
        await _dbContext.LedgerJournals.AddAsync(journal, cancellationToken);
    }

    public async Task RecordRefundAsync(
        Payment payment,
        Refund refund,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var externalReference = $"refund:{refund.Id:N}";
        if (await ExistsAsync(externalReference, cancellationToken))
        {
            return;
        }

        var journal = LedgerJournal.Create(
            payment.MerchantId,
            payment.Id,
            externalReference,
            payment.Currency,
            "Reverse merchant payable for provider refund",
            [
                new LedgerLineDraft(LedgerAccount.MerchantPayable, LedgerDirection.Debit, refund.AmountMinor),
                new LedgerLineDraft(LedgerAccount.ProviderClearing, LedgerDirection.Credit, refund.AmountMinor)
            ],
            now);
        await _dbContext.LedgerJournals.AddAsync(journal, cancellationToken);
    }

    public async Task RecordSettlementAsync(
        SettlementBatch settlement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var externalReference = $"settlement:{settlement.Id:N}";
        if (await ExistsAsync(externalReference, cancellationToken))
        {
            return;
        }

        var journal = LedgerJournal.Create(
            settlement.MerchantId,
            null,
            externalReference,
            settlement.Currency,
            "Transfer merchant payable into settlement clearing",
            [
                new LedgerLineDraft(LedgerAccount.MerchantPayable, LedgerDirection.Debit, settlement.AmountMinor),
                new LedgerLineDraft(LedgerAccount.SettlementClearing, LedgerDirection.Credit, settlement.AmountMinor)
            ],
            now);
        foreach (var line in journal.Lines)
        {
            line.AssignToSettlement(settlement.Id);
        }

        await _dbContext.LedgerJournals.AddAsync(journal, cancellationToken);
    }

    private Task<bool> ExistsAsync(string externalReference, CancellationToken cancellationToken) =>
        _dbContext.LedgerJournals.AnyAsync(
            journal => journal.ExternalReference == externalReference,
            cancellationToken);
}
