namespace SentinelPay.Domain.Ledger;

public enum LedgerAccount
{
    ProviderClearing = 0,
    MerchantPayable = 1,
    SettlementClearing = 2
}

public enum LedgerDirection
{
    Debit = 0,
    Credit = 1
}
