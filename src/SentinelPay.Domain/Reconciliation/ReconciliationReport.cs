namespace SentinelPay.Domain.Reconciliation;

public enum ReconciliationReportStatus
{
    Matched,
    ReviewRequired
}

public enum ReconciliationIssueType
{
    MissingLocally,
    MissingAtProvider,
    AuthorizedAmountMismatch,
    CapturedAmountMismatch,
    CurrencyMismatch,
    StateMismatch
}

public sealed class ReconciliationReport
{
    private readonly List<ReconciliationIssue> _issues = [];

    private ReconciliationReport()
    {
    }

    private ReconciliationReport(
        Guid id,
        Guid merchantId,
        string provider,
        string sourceFileName,
        string sourceSha256,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int providerRowCount,
        DateTimeOffset createdAt)
    {
        Id = id;
        MerchantId = merchantId;
        Provider = provider;
        SourceFileName = sourceFileName;
        SourceSha256 = sourceSha256;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        ProviderRowCount = providerRowCount;
        Status = ReconciliationReportStatus.Matched;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string SourceFileName { get; private set; } = string.Empty;
    public string SourceSha256 { get; private set; } = string.Empty;
    public DateTimeOffset PeriodStart { get; private set; }
    public DateTimeOffset PeriodEnd { get; private set; }
    public int ProviderRowCount { get; private set; }
    public int MatchedRowCount { get; private set; }
    public ReconciliationReportStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyCollection<ReconciliationIssue> Issues => _issues.AsReadOnly();

    public static ReconciliationReport Create(
        Guid merchantId,
        string provider,
        string sourceFileName,
        string sourceSha256,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int providerRowCount,
        DateTimeOffset now)
    {
        if (merchantId == Guid.Empty)
        {
            throw new DomainException("Merchant id is required.");
        }

        if (periodEnd <= periodStart)
        {
            throw new DomainException("Reconciliation period end must be after its start.");
        }

        if (providerRowCount < 0)
        {
            throw new DomainException("Provider row count cannot be negative.");
        }

        return new ReconciliationReport(
            Guid.NewGuid(),
            merchantId,
            RequireBounded(provider, 40, "Provider"),
            RequireBounded(sourceFileName, 240, "Source file name"),
            RequireSha256(sourceSha256),
            periodStart,
            periodEnd,
            providerRowCount,
            now);
    }

    public void RecordMatch() => MatchedRowCount++;

    public void AddIssue(
        ReconciliationIssueType type,
        string providerReference,
        Guid? paymentId,
        string details,
        DateTimeOffset now)
    {
        _issues.Add(new ReconciliationIssue(
            Guid.NewGuid(),
            Id,
            type,
            RequireBounded(providerReference, 120, "Provider reference"),
            paymentId,
            RequireBounded(details, 1000, "Issue details"),
            now));
        Status = ReconciliationReportStatus.ReviewRequired;
    }

    private static string RequireSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new DomainException("Source hash must be a SHA-256 hexadecimal digest.");
        }

        return value;
    }

    private static string RequireBounded(string value, int maxLength, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength)
        {
            throw new DomainException($"{fieldName} is required and cannot exceed {maxLength} characters.");
        }

        return normalized;
    }
}

public sealed class ReconciliationIssue
{
    private ReconciliationIssue()
    {
    }

    internal ReconciliationIssue(
        Guid id,
        Guid reportId,
        ReconciliationIssueType type,
        string providerReference,
        Guid? paymentId,
        string details,
        DateTimeOffset createdAt)
    {
        Id = id;
        ReportId = reportId;
        Type = type;
        ProviderReference = providerReference;
        PaymentId = paymentId;
        Details = details;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid ReportId { get; private set; }
    public ReconciliationIssueType Type { get; private set; }
    public string ProviderReference { get; private set; } = string.Empty;
    public Guid? PaymentId { get; private set; }
    public string Details { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
