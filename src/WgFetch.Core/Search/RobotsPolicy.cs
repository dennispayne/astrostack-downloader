using System.Collections.Concurrent;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>
/// Fetches and caches <c>robots.txt</c> per host and is consulted before any page fetch
/// (docs/REQUIREMENTS.md, "Web search": "Honor robots.txt"). Fails open (allows the fetch) when
/// <c>robots.txt</c> cannot be retrieved at all, since an unreachable robots file is not a
/// disallow — but a successfully retrieved disallow is always honoured.
/// </summary>
public sealed class RobotsPolicy
{
    private readonly IHttpGateway _http;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _disallowByHost = new(StringComparer.OrdinalIgnoreCase);

    public RobotsPolicy(IHttpGateway http)
    {
        _http = http;
    }

    /// <summary>True when <paramref name="url"/> may be fetched under the target host's robots.txt.</summary>
    public async Task<bool> IsAllowedAsync(Uri url, string userAgent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        var disallows = await GetDisallowRulesAsync(url, cancellationToken).ConfigureAwait(false);
        var path = url.PathAndQuery;

        foreach (var rule in disallows)
        {
            if (rule.Length == 0)
            {
                continue;
            }

            if (path.StartsWith(rule, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<IReadOnlyList<string>> GetDisallowRulesAsync(Uri url, CancellationToken cancellationToken)
    {
        var host = url.Host;
        if (_disallowByHost.TryGetValue(host, out var cached))
        {
            return cached;
        }

        var rules = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
        _disallowByHost[host] = rules;
        return rules;
    }

    private async Task<IReadOnlyList<string>> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        var robotsUrl = new Uri($"{url.Scheme}://{url.Authority}/robots.txt");
        try
        {
            var response = await _http.SendAsync(
                new HttpRequestSpec { Url = robotsUrl, Verb = HttpVerb.Get },
                cancellationToken).ConfigureAwait(false);

            await using (response.ConfigureAwait(false))
            {
                if (!response.IsSuccess)
                {
                    return Array.Empty<string>();
                }

                using var reader = new StreamReader(response.Body);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return ParseDisallowRules(text);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Unreachable robots.txt is not a disallow; fail open.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Parses the <c>Disallow</c> rules under a <c>User-agent: *</c> block (wildcard rules are the
    /// only ones consulted). Malformed input yields an empty rule set rather than throwing.
    /// </summary>
    internal static IReadOnlyList<string> ParseDisallowRules(string robotsTxt)
    {
        var rules = new List<string>();
        var applies = false;

        foreach (var rawLine in robotsTxt.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (string.Equals(key, "User-agent", StringComparison.OrdinalIgnoreCase))
            {
                applies = value == "*";
            }
            else if (applies && string.Equals(key, "Disallow", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                rules.Add(value);
            }
        }

        return rules;
    }
}
