namespace SentinelPay.Domain.Ledger;

public sealed class LedgerJournal
{
    private readonly List<LedgerLine> _lines = [];

    private LedgerJournal()
    {
    }

    private LedgerJournal(
        Guid id,
        Guid merchantId,
        Guid? paymentId,
        string externalReference,
        string currency,
        string description,
        DateTimeOffset createdAt)
    {
        Id = id;
        MerchantId = merchantId;
        PaymentId = paymentId;
        ExternalReference = externalReference;
        Currency = currency;
        Description = description;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid? PaymentId { get; private set; }
    public string ExternalReference { get; private set; } = string.Empty;
    public string Currency { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyCollection<LedgerLine> Lines => _lines.AsReadOnly();

    public static LedgerJournal Create(
        Guid merchantId,
        Guid? paymentId,
        string externalReference,
        string currency,
        string description,
        IEnumerable<LedgerLineDraft> lineDrafts,
        DateTimeOffset now)
    {
        var drafts = lineDrafts.ToArray();
        if (merchantId == Guid.Empty || string.IsNullOrWhiteSpace(externalReference))
        {
            throw new DomainException("Ledger merchant and external reference are required.");
        }

        if (currency.Length != 3)
        {
            throw new DomainException("Ledger currency must be an ISO 4217 code.");
        }

        if (drafts.Length < 2 || drafts.Any(line => line.AmountMinor <= 0))
        {
            throw new DomainException("A journal requires at least two positive ledger lines.");
        }

        var debits = drafts.Where(line => line.Direction == LedgerDirection.Debit).Sum(line => line.AmountMinor);
        var credits = drafts.Where(line => line.Direction == LedgerDirection.Credit).Sum(line => line.AmountMinor);
        if (debits != credits)
        {
            throw new DomainException($"Ledger journal is unbalanced: debits={debits}, credits={credits}.");
        }

        var journal = new LedgerJournal(
            Guid.NewGuid(),
            merchantId,
            paymentId,
            externalReference.Trim(),
            currency.ToUpperInvariant(),
            description.Trim(),
            now);

        foreach (var draft in drafts)
        {
            journal._lines.Add(new LedgerLine(
                Guid.NewGuid(),
                journal.Id,
                merchantId,
                paymentId,
                draft.Account,
                draft.Direction,
                draft.AmountMinor,
                now));
        }

        return journal;
    }
}

public sealed record LedgerLineDraft(LedgerAccount Account, LedgerDirection Direction, long AmountMinor);
