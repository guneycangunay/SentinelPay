using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Payments;
using SentinelPay.Domain.Ledger;
using SentinelPay.Domain.Settlements;

namespace SentinelPay.Application.Settlements;

public sealed class SettlementService
{
    private readonly ISettlementStore _store;
    private readonly ILedgerWriter _ledger;
    private readonly IDistributedLock _distributedLock;
    private readonly IOutboxWriter _outbox;
    private readonly IClock _clock;

    public SettlementService(
        ISettlementStore store,
        ILedgerWriter ledger,
        IDistributedLock distributedLock,
        IOutboxWriter outbox,
        IClock clock)
    {
        _store = store;
        _ledger = ledger;
        _distributedLock = distributedLock;
        _outbox = outbox;
        _clock = clock;
    }

    public async Task<SettlementResult> CreateAsync(
        CreateSettlementCommand command,
        CancellationToken cancellationToken)
    {
        if (command.MerchantId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.IdempotencyKey) ||
            command.IdempotencyKey.Length is < 8 or > 128)
        {
            throw new ArgumentException("Merchant and a valid idempotency key are required.");
        }

        var currency = command.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3)
        {
            throw new ArgumentException("Currency must be a three-letter ISO 4217 code.");
        }

        await using var lease = await _distributedLock.AcquireAsync(
            $"settlement:{command.MerchantId:N}:{currency}",
            TimeSpan.FromSeconds(30),
            cancellationToken);

        var existing = await _store.GetByIdempotencyKeyAsync(
            command.MerchantId,
            command.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Currency != currency || existing.PeriodEnd != command.PeriodEnd)
            {
                throw new IdempotencyConflictException(
                    "The settlement idempotency key was already used with a different currency or period end.");
            }

            return new SettlementResult(SettlementResponse.From(existing), true);
        }

        var lines = await _store.GetUnsettledPayableLinesAsync(
            command.MerchantId,
            currency,
            command.PeriodEnd,
            cancellationToken);
        var amountMinor = lines.Sum(line =>
            line.Direction == LedgerDirection.Credit ? line.AmountMinor : -line.AmountMinor);
        if (amountMinor <= 0)
        {
            throw new NoPayableBalanceException(currency);
        }

        var settlement = SettlementBatch.Create(
            command.MerchantId,
            currency,
            amountMinor,
            command.IdempotencyKey,
            command.PeriodEnd,
            _clock.UtcNow);
        foreach (var line in lines)
        {
            line.AssignToSettlement(settlement.Id);
        }

        await _store.AddAsync(settlement, cancellationToken);
        await _ledger.RecordSettlementAsync(settlement, _clock.UtcNow, cancellationToken);
        _outbox.Add(
            "settlement.created.v1",
            settlement.Id,
            new
            {
                settlement.Id,
                settlement.MerchantId,
                settlement.Currency,
                settlement.AmountMinor,
                settlement.PeriodEnd
            },
            _clock.UtcNow);
        await _store.SaveChangesAsync(cancellationToken);
        return new SettlementResult(SettlementResponse.From(settlement), false);
    }

    public async Task<SettlementResponse> GetAsync(
        Guid merchantId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        var settlement = await _store.GetAsync(merchantId, settlementId, cancellationToken)
            ?? throw new KeyNotFoundException($"Settlement '{settlementId}' was not found.");
        return SettlementResponse.From(settlement);
    }
}
