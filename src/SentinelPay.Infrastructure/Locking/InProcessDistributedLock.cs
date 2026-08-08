using System.Collections.Concurrent;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Infrastructure.Locking;

public sealed class InProcessDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(
        string resource,
        TimeSpan expiry,
        CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(expiry, cancellationToken))
        {
            throw new TimeoutException($"Could not acquire lock for '{resource}'.");
        }

        return new Lease(semaphore);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _released;

        public Lease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
