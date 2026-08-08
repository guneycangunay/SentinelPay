using SentinelPay.Domain.Ledger;
using SentinelPay.Domain.Settlements;

namespace SentinelPay.Application.Abstractions;

public interface ISettlementStore
{
    Task<IReadOnlyList<LedgerLine>> GetUnsettledPayableLinesAsync(
        Guid merchantId,
        string currency,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken);

    Task<SettlementBatch?> GetAsync(Guid merchantId, Guid settlementId, CancellationToken cancellationToken);
    Task<SettlementBatch?> GetByIdempotencyKeyAsync(
        Guid merchantId,
        string idempotencyKey,
        CancellationToken cancellationToken);
    Task AddAsync(SettlementBatch settlement, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
