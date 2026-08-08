using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Reconciliation;
using SentinelPay.Domain;
using SentinelPay.Domain.Payments;
using SentinelPay.Domain.Reconciliation;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Reconciliation;

public sealed class ReconciliationImportService : IReconciliationImportService
{
    private readonly SentinelPayDbContext _dbContext;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly IDistributedLock _distributedLock;
    private readonly IClock _clock;

    public ReconciliationImportService(
        SentinelPayDbContext dbContext,
        IPaymentGatewayResolver gatewayResolver,
        IDistributedLock distributedLock,
        IClock clock)
    {
        _dbContext = dbContext;
        _gatewayResolver = gatewayResolver;
        _distributedLock = distributedLock;
        _clock = clock;
    }

    public async Task<ReconciliationReportResponse> ImportAsync(
        ImportReconciliationReportCommand command,
        CancellationToken cancellationToken)
    {
        if (command.MerchantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Merchant identity is required.");
        }

        if (command.PeriodEnd <= command.PeriodStart || command.PeriodEnd - command.PeriodStart > TimeSpan.FromDays(32))
        {
            throw new DomainException("Reconciliation periods must be positive and cannot exceed 32 days.");
        }

        var provider = _gatewayResolver.Resolve(command.Provider).Name;
        var rows = ProviderReportCsvReader.Read(command.Csv);
        if (rows.Any(row => row.OccurredAt < command.PeriodStart || row.OccurredAt >= command.PeriodEnd))
        {
            throw new DomainException("Every provider row must fall inside the requested half-open reconciliation period.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Csv)));
        await using var lease = await _distributedLock.AcquireAsync(
            $"reconciliation:import:{command.MerchantId:N}:{provider}:{hash}",
            TimeSpan.FromSeconds(30),
            cancellationToken);
        var existing = await _dbContext.ReconciliationReports
            .AsNoTracking()
            .Include(report => report.Issues)
            .SingleOrDefaultAsync(
                report => report.MerchantId == command.MerchantId &&
                          report.Provider == provider &&
                          report.SourceSha256 == hash,
                cancellationToken);
        if (existing is not null)
        {
            return Map(existing);
        }

        var localPayments = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.MerchantId == command.MerchantId &&
                payment.Provider == provider &&
                payment.CreatedAt >= command.PeriodStart &&
                payment.CreatedAt < command.PeriodEnd &&
                payment.ProviderReference != null)
            .ToArrayAsync(cancellationToken);
        var localByReference = localPayments.ToDictionary(
            payment => payment.ProviderReference!,
            StringComparer.Ordinal);
        var report = ReconciliationReport.Create(
            command.MerchantId,
            provider,
            SafeFileName(command.SourceFileName),
            hash,
            command.PeriodStart,
            command.PeriodEnd,
            rows.Count,
            _clock.UtcNow);

        foreach (var row in rows)
        {
            if (!localByReference.TryGetValue(row.ProviderReference, out var payment))
            {
                report.AddIssue(
                    ReconciliationIssueType.MissingLocally,
                    row.ProviderReference,
                    null,
                    "The provider reported a payment that is absent from the merchant's local payment set.",
                    _clock.UtcNow);
                continue;
            }

            var issueCountBefore = report.Issues.Count;
            Compare(report, payment, row);
            if (report.Issues.Count == issueCountBefore)
            {
                report.RecordMatch();
            }
        }

        var providerReferences = rows.Select(row => row.ProviderReference).ToHashSet(StringComparer.Ordinal);
        foreach (var payment in localPayments.Where(payment => !providerReferences.Contains(payment.ProviderReference!)))
        {
            report.AddIssue(
                ReconciliationIssueType.MissingAtProvider,
                payment.ProviderReference!,
                payment.Id,
                "The local payment is absent from the provider report for the selected period.",
                _clock.UtcNow);
        }

        await _dbContext.ReconciliationReports.AddAsync(report, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(report);
    }

    private void Compare(ReconciliationReport report, Payment payment, ProviderReportRow row)
    {
        var now = _clock.UtcNow;
        if (payment.AmountMinor != row.AuthorizedAmountMinor)
        {
            report.AddIssue(
                ReconciliationIssueType.AuthorizedAmountMismatch,
                row.ProviderReference,
                payment.Id,
                $"Local authorized amount is {payment.AmountMinor}; provider amount is {row.AuthorizedAmountMinor}.",
                now);
        }

        if (payment.CapturedAmountMinor != row.CapturedAmountMinor)
        {
            report.AddIssue(
                ReconciliationIssueType.CapturedAmountMismatch,
                row.ProviderReference,
                payment.Id,
                $"Local captured amount is {payment.CapturedAmountMinor}; provider amount is {row.CapturedAmountMinor}.",
                now);
        }

        if (!payment.Currency.Equals(row.Currency, StringComparison.Ordinal))
        {
            report.AddIssue(
                ReconciliationIssueType.CurrencyMismatch,
                row.ProviderReference,
                payment.Id,
                $"Local currency is {payment.Currency}; provider currency is {row.Currency}.",
                now);
        }

        var localState = ToProviderState(payment.Status);
        if (!localState.Equals(row.State, StringComparison.Ordinal))
        {
            report.AddIssue(
                ReconciliationIssueType.StateMismatch,
                row.ProviderReference,
                payment.Id,
                $"Local state is {localState}; provider state is {row.State}.",
                now);
        }
    }

    private static string ToProviderState(PaymentStatus status) => status switch
    {
        PaymentStatus.RequiresAction => "requires_action",
        PaymentStatus.Authorized => "authorized",
        PaymentStatus.PartiallyCaptured => "partially_captured",
        PaymentStatus.Captured => "captured",
        PaymentStatus.PartiallyCapturedAndVoided => "partially_captured_and_voided",
        PaymentStatus.Voided => "voided",
        PaymentStatus.PartiallyRefunded => "partially_refunded",
        PaymentStatus.Refunded => "refunded",
        PaymentStatus.Failed => "failed",
        PaymentStatus.Expired => "expired",
        _ => "pending"
    };

    private static string SafeFileName(string fileName)
    {
        var safe = Path.GetFileName(fileName?.Trim());
        if (string.IsNullOrWhiteSpace(safe) || safe.Length > 240)
        {
            throw new DomainException("A source file name of at most 240 characters is required.");
        }

        return safe;
    }

    private static ReconciliationReportResponse Map(ReconciliationReport report) => new(
        report.Id,
        report.Provider,
        report.SourceFileName,
        report.SourceSha256,
        report.PeriodStart,
        report.PeriodEnd,
        report.ProviderRowCount,
        report.MatchedRowCount,
        report.Status,
        report.CreatedAt,
        report.Issues
            .OrderBy(issue => issue.CreatedAt)
            .Select(issue => new ReconciliationIssueResponse(
                issue.Type,
                issue.ProviderReference,
                issue.PaymentId,
                issue.Details))
            .ToArray());
}
