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
        var normalizedExternalReference = externalReference?.Trim() ?? string.Empty;
        var normalizedCurrency = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        var normalizedDescription = description?.Trim() ?? string.Empty;
        if (merchantId == Guid.Empty || normalizedExternalReference.Length is 0 or > 160)
        {
            throw new DomainException("Ledger merchant and an external reference up to 160 characters are required.");
        }

        if (normalizedCurrency.Length != 3 ||
            normalizedCurrency.Any(character => !char.IsLetter(character)))
        {
            throw new DomainException("Ledger currency must be an ISO 4217 code.");
        }

        if (normalizedDescription.Length is 0 or > 240)
        {
            throw new DomainException("Ledger description is required and cannot exceed 240 characters.");
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
            normalizedExternalReference,
            normalizedCurrency,
            normalizedDescription,
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
