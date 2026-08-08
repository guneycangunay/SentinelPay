using SentinelPay.Domain.Reconciliation;

namespace SentinelPay.Application.Reconciliation;

public sealed record ImportReconciliationReportCommand(
    Guid MerchantId,
    string Provider,
    string SourceFileName,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string Csv);

public sealed record ReconciliationIssueResponse(
    ReconciliationIssueType Type,
    string ProviderReference,
    Guid? PaymentId,
    string Details);

public sealed record ReconciliationReportResponse(
    Guid Id,
    string Provider,
    string SourceFileName,
    string SourceSha256,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int ProviderRowCount,
    int MatchedRowCount,
    ReconciliationReportStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<ReconciliationIssueResponse> Issues);
