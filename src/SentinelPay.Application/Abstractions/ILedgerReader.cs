namespace SentinelPay.Application.Abstractions;

public interface ILedgerReader
{
    Task<IReadOnlyCollection<LedgerAccountBalance>> GetBalancesAsync(
        Guid merchantId,
        string currency,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<LedgerJournalSummary>> GetRecentJournalsAsync(
        Guid merchantId,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record LedgerAccountBalance(
    string Account,
    string Currency,
    long DebitsMinor,
    long CreditsMinor,
    long NetMinor);

public sealed record LedgerJournalSummary(
    Guid Id,
    Guid? PaymentId,
    string ExternalReference,
    string Currency,
    string Description,
    DateTimeOffset CreatedAt);
