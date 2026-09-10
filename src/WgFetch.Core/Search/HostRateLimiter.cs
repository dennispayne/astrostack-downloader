using System.Collections.Concurrent;

namespace WgFetch.Core.Search;

/// <summary>
/// A polite per-host delay so search providers and page fetches never hammer a host
/// (docs/REQUIREMENTS.md, "Web search": "rate-limit politely").
/// </summary>
public sealed class HostRateLimiter
{
    private readonly TimeSpan _minDelay;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public HostRateLimiter(TimeSpan? minDelay = null, TimeProvider? clock = null)
    {
        _minDelay = minDelay ?? TimeSpan.FromSeconds(1);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Waits, if necessary, until the minimum delay since the last request to this host has elapsed.</summary>
    public async Task WaitAsync(string host, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(host, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest.TryGetValue(host, out var last))
            {
                var elapsed = _clock.GetUtcNow() - last;
                var remaining = _minDelay - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _clock, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastRequest[host] = _clock.GetUtcNow();
        }
        finally
        {
            gate.Release();
        }
    }
}
