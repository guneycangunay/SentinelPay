using SentinelPay.Application.Reconciliation;

namespace SentinelPay.Application.Abstractions;

public interface IReconciliationImportService
{
    Task<ReconciliationReportResponse> ImportAsync(
        ImportReconciliationReportCommand command,
        CancellationToken cancellationToken);
}
