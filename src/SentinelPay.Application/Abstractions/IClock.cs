namespace SentinelPay.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
