namespace SentinelPay.Application.Abstractions;

public interface IDistributedLock
{
    Task<IAsyncDisposable> AcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken);
}
