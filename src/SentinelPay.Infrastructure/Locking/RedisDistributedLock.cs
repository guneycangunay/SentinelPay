using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SentinelPay.Application.Abstractions;
using StackExchange.Redis;

namespace SentinelPay.Infrastructure.Locking;

public sealed class RedisDistributedLock : IDistributedLock
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(75);
    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisDistributedLock> _logger;

    public RedisDistributedLock(
        IConnectionMultiplexer connection,
        ILogger<RedisDistributedLock> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        string resource,
        TimeSpan expiry,
        CancellationToken cancellationToken)
    {
        var database = _connection.GetDatabase();
        var key = (RedisKey)$"sentinelpay:lock:{resource}";
        var token = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (stopwatch.Elapsed < expiry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await database.StringSetAsync(key, token, expiry, When.NotExists))
                {
                    return new Lease(database, key, token, _logger);
                }

                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
        catch (RedisException exception)
        {
            throw new DistributedLockUnavailableException(exception);
        }

        throw new TimeoutException($"Could not acquire distributed lock for '{resource}'.");
    }

    private sealed class Lease : IAsyncDisposable
    {
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
        private readonly IDatabase _database;
        private readonly RedisKey _key;
        private readonly RedisValue _token;
        private readonly ILogger _logger;
        private int _released;

        public Lease(IDatabase database, RedisKey key, RedisValue token, ILogger logger)
        {
            _database = database;
            _key = key;
            _token = token;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try
                {
                    await _database.ScriptEvaluateAsync(ReleaseScript, [_key], [_token]);
                }
                catch (RedisException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not release a Redis lease; it will expire automatically.");
                }
            }
        }
    }
}
