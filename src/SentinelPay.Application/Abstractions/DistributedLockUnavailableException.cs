namespace SentinelPay.Application.Abstractions;

public sealed class DistributedLockUnavailableException : Exception
{
    public DistributedLockUnavailableException(Exception innerException)
        : base("The distributed coordination service is temporarily unavailable.", innerException)
    {
    }
}
