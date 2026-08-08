using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
