using Microsoft.EntityFrameworkCore;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Ledger;
using SentinelPay.Domain.Settlements;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Settlements;

public sealed class SettlementStore : ISettlementStore
{
    private readonly SentinelPayDbContext _dbContext;

    public SettlementStore(SentinelPayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<LedgerLine>> GetUnsettledPayableLinesAsync(
        Guid merchantId,
        string currency,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken) =>
        await (
            from line in _dbContext.LedgerLines
            join journal in _dbContext.LedgerJournals on line.JournalId equals journal.Id
            where line.MerchantId == merchantId &&
                  line.Account == LedgerAccount.MerchantPayable &&
                  line.SettlementBatchId == null &&
                  line.CreatedAt <= periodEnd &&
                  journal.Currency == currency
            orderby line.CreatedAt
            select line)
            .ToListAsync(cancellationToken);

    public Task<SettlementBatch?> GetAsync(
        Guid merchantId,
        Guid settlementId,
        CancellationToken cancellationToken) =>
        _dbContext.SettlementBatches.SingleOrDefaultAsync(
            settlement => settlement.MerchantId == merchantId && settlement.Id == settlementId,
            cancellationToken);

    public Task<SettlementBatch?> GetByIdempotencyKeyAsync(
        Guid merchantId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        _dbContext.SettlementBatches.SingleOrDefaultAsync(
            settlement => settlement.MerchantId == merchantId && settlement.IdempotencyKey == idempotencyKey,
            cancellationToken);

    public async Task AddAsync(SettlementBatch settlement, CancellationToken cancellationToken)
    {
        await _dbContext.SettlementBatches.AddAsync(settlement, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
