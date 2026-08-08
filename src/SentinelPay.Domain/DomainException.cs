namespace SentinelPay.Domain;

public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
