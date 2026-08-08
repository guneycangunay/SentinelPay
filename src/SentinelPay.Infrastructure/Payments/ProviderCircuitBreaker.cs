using SentinelPay.Application.Abstractions;
using System.Diagnostics.Metrics;

namespace SentinelPay.Infrastructure.Payments;

public sealed class ProviderCircuitBreaker
{
    public const string MeterName = "SentinelPay.Provider";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Opened = Meter.CreateCounter<long>("sentinelpay.provider.circuit.opened");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("sentinelpay.provider.circuit.rejected");
    private const int FailureThreshold = 5;
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(15);
    private readonly object _sync = new();
    private readonly IClock _clock;
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _halfOpenProbeInProgress;

    public ProviderCircuitBreaker(IClock clock)
    {
        _clock = clock;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            var result = await operation().ConfigureAwait(false);
            RecordSuccess();
            return result;
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            throw;
        }
        catch (TimeoutException)
        {
            RecordFailure();
            throw;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordFailure();
            throw;
        }
    }

    private void Enter()
    {
        lock (_sync)
        {
            if (_openUntil is null)
            {
                return;
            }

            if (_openUntil > _clock.UtcNow)
            {
                Rejected.Add(1, new KeyValuePair<string, object?>("provider", "acquirer-http"));
                throw new HttpRequestException("Provider circuit is open; no remote request was attempted.");
            }

            if (_halfOpenProbeInProgress)
            {
                Rejected.Add(1, new KeyValuePair<string, object?>("provider", "acquirer-http"));
                throw new HttpRequestException("Provider circuit is half-open and already has a probe in flight.");
            }

            _halfOpenProbeInProgress = true;
        }
    }

    private void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
            _halfOpenProbeInProgress = false;
        }
    }

    private void RecordFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            if (_halfOpenProbeInProgress || _consecutiveFailures >= FailureThreshold)
            {
                _openUntil = _clock.UtcNow.Add(BreakDuration);
                Opened.Add(1, new KeyValuePair<string, object?>("provider", "acquirer-http"));
            }

            _halfOpenProbeInProgress = false;
        }
    }
}
