using SentinelPay.Domain.Payments;
using SentinelPay.Domain.Settlements;

namespace SentinelPay.Application.Abstractions;

public interface ILedgerWriter
{
    Task RecordCaptureAsync(Payment payment, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordRefundAsync(Payment payment, Refund refund, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordSettlementAsync(SettlementBatch settlement, DateTimeOffset now, CancellationToken cancellationToken);
}
