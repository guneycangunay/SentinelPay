namespace SentinelPay.Domain.Ledger;

public sealed class LedgerLine
{
    private LedgerLine()
    {
    }

    internal LedgerLine(
        Guid id,
        Guid journalId,
        Guid merchantId,
        Guid? paymentId,
        LedgerAccount account,
        LedgerDirection direction,
        long amountMinor,
        DateTimeOffset createdAt)
    {
        Id = id;
        JournalId = journalId;
        MerchantId = merchantId;
        PaymentId = paymentId;
        Account = account;
        Direction = direction;
        AmountMinor = amountMinor;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid JournalId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid? PaymentId { get; private set; }
    public Guid? SettlementBatchId { get; private set; }
    public LedgerAccount Account { get; private set; }
    public LedgerDirection Direction { get; private set; }
    public long AmountMinor { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void AssignToSettlement(Guid settlementBatchId)
    {
        if (SettlementBatchId is not null && SettlementBatchId != settlementBatchId)
        {
            throw new DomainException("Ledger line is already assigned to another settlement.");
        }

        SettlementBatchId = settlementBatchId;
    }
}
