using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Discovery;

/// <summary>
/// Centralized GitHub API rate-limit accounting so a burst of parallel calls cannot blow the
/// anonymous 60/hr (or 5000/hr authenticated) ceiling (docs/REQUIREMENTS.md, "Discovery pipeline",
/// "GitHub token"). Thread-safe: every caller observes responses through the same instance.
/// </summary>
public sealed class GitHubRateLimiter
{
    private readonly object _lock = new();
    private int? _remaining;
    private DateTimeOffset? _resetAt;
    private TimeSpan? _retryAfter;
    private DateTimeOffset? _retryAfterObservedAt;

    /// <summary>Remaining calls as of the last observed response, or null if never observed.</summary>
    public int? Remaining
    {
        get { lock (_lock) { return _remaining; } }
    }

    /// <summary>True when the last observed response indicated the ceiling has been hit.</summary>
    public bool IsExhausted
    {
        get
        {
            lock (_lock)
            {
                return _remaining is 0 && _resetAt is { } reset && reset > DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>How long callers should wait before the next GitHub API call, honouring the last <c>Retry-After</c>.</summary>
    public TimeSpan? RecommendedWait
    {
        get
        {
            lock (_lock)
            {
                if (_retryAfter is { } retryAfter && _retryAfterObservedAt is { } observedAt)
                {
                    var remaining = retryAfter - (DateTimeOffset.UtcNow - observedAt);
                    if (remaining > TimeSpan.Zero)
                    {
                        return remaining;
                    }
                }

                if (_remaining is 0 && _resetAt is { } reset)
                {
                    var wait = reset - DateTimeOffset.UtcNow;
                    return wait > TimeSpan.Zero ? wait : null;
                }

                return null;
            }
        }
    }

    /// <summary>Records the rate-limit state from a GitHub API response.</summary>
    public void Observe(HttpResponseSpec response)
    {
        lock (_lock)
        {
            if (int.TryParse(response.Header("X-RateLimit-Remaining"), out var remaining))
            {
                _remaining = remaining;
            }

            if (long.TryParse(response.Header("X-RateLimit-Reset"), out var resetEpoch))
            {
                _resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            }

            var retryAfterHeader = response.Header("Retry-After");
            if (int.TryParse(retryAfterHeader, out var retryAfterSeconds))
            {
                _retryAfter = TimeSpan.FromSeconds(retryAfterSeconds);
                _retryAfterObservedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>True when a response indicates rate limiting (403/429 with a rate-limit signal), not merely "not found".</summary>
    public static bool LooksRateLimited(HttpResponseSpec response) =>
        response.StatusCode is 429 ||
        (response.StatusCode == 403 &&
         (string.Equals(response.Header("X-RateLimit-Remaining"), "0", StringComparison.Ordinal) ||
          response.Header("Retry-After") is not null));
}
