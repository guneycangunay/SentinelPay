namespace SentinelPay.Domain.Merchants;

public sealed class Merchant
{
    private Merchant()
    {
    }

    private Merchant(Guid id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Status = MerchantStatus.Active;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public MerchantStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Merchant Create(Guid id, string name, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Merchant id is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Merchant name is required.");
        }

        return new Merchant(id, name.Trim(), now);
    }

    public void Suspend() => Status = MerchantStatus.Suspended;
}
