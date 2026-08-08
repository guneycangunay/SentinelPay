using Microsoft.EntityFrameworkCore;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Ledger;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Ledger;

public sealed class LedgerReader : ILedgerReader
{
    private readonly SentinelPayDbContext _dbContext;

    public LedgerReader(SentinelPayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<LedgerAccountBalance>> GetBalancesAsync(
        Guid merchantId,
        string currency,
        CancellationToken cancellationToken)
    {
        var normalizedCurrency = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedCurrency.Length != 3 ||
            normalizedCurrency.Any(character => !char.IsLetter(character)))
        {
            throw new ArgumentException("Currency must be a three-letter ISO 4217 code.");
        }

        var rawBalances = await (
            from line in _dbContext.LedgerLines.AsNoTracking()
            join journal in _dbContext.LedgerJournals.AsNoTracking() on line.JournalId equals journal.Id
            where line.MerchantId == merchantId && journal.Currency == normalizedCurrency
            group line by line.Account into account
            select new
            {
                Account = account.Key,
                Debits = account.Where(line => line.Direction == LedgerDirection.Debit).Sum(line => line.AmountMinor),
                Credits = account.Where(line => line.Direction == LedgerDirection.Credit).Sum(line => line.AmountMinor)
            }).ToListAsync(cancellationToken);

        return Enum.GetValues<LedgerAccount>()
            .Select(account =>
            {
                var raw = rawBalances.SingleOrDefault(balance => balance.Account == account);
                var debits = raw?.Debits ?? 0;
                var credits = raw?.Credits ?? 0;
                return new LedgerAccountBalance(
                    account.ToString(),
                    normalizedCurrency,
                    debits,
                    credits,
                    credits - debits);
            })
            .ToArray();
    }

    public async Task<IReadOnlyCollection<LedgerJournalSummary>> GetRecentJournalsAsync(
        Guid merchantId,
        int limit,
        CancellationToken cancellationToken) =>
        await _dbContext.LedgerJournals
            .AsNoTracking()
            .Where(journal => journal.MerchantId == merchantId)
            .OrderByDescending(journal => journal.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(journal => new LedgerJournalSummary(
                journal.Id,
                journal.PaymentId,
                journal.ExternalReference,
                journal.Currency,
                journal.Description,
                journal.CreatedAt))
            .ToArrayAsync(cancellationToken);
}
