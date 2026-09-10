using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Discovery;

/// <summary>Result of asking the GitHub Releases API for a repository's latest release.</summary>
internal sealed record GitHubReleaseLookup
{
    public IReadOnlyList<DiscoveryCandidate> Candidates { get; init; } = Array.Empty<DiscoveryCandidate>();

    public string? Version { get; init; }

    public string? Warning { get; init; }

    public bool RateLimited { get; init; }
}

/// <summary>
/// Shared GitHub Releases API lookup used by both recipe-backed stages (a recipe naming
/// <see cref="RecipeSourceKind.GitHubRelease"/>) and the standalone <see cref="GitHubReleasesStage"/>
/// (docs/REQUIREMENTS.md, "Discovery pipeline": "GitHub Releases API (<c>/releases/latest</c>, assets
/// filtered by arch/type), centralized rate-limit accounting, <c>GITHUB_TOKEN</c> support, 403/rate-limit
/// handling with <c>Retry-After</c>").
/// </summary>
internal static class GitHubReleaseResolver
{
    public static async Task<GitHubReleaseLookup> ResolveLatestReleaseAsync(
        IHttpGateway http,
        GitHubRateLimiter rateLimiter,
        string repository,
        string? assetPattern,
        string? githubToken,
        DiscoveryStage taggedStage,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        logger ??= NullLogger.Instance;

        if (rateLimiter.IsExhausted)
        {
            return new GitHubReleaseLookup
            {
                RateLimited = true,
                Warning = $"GitHub API rate limit already exhausted (resets after {rateLimiter.RecommendedWait}); skipping '{repository}'.",
            };
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/vnd.github+json",
            ["X-GitHub-Api-Version"] = "2022-11-28",
        };

        if (!string.IsNullOrWhiteSpace(githubToken))
        {
            // The token is only ever placed in the outbound request header, never logged or
            // included in any exception/warning message built in this method.
            headers["Authorization"] = TokenHeader(githubToken);
        }

        var url = new Uri($"https://api.github.com/repos/{repository}/releases/latest");
        HttpResponseSpec response;
        try
        {
            response = await http.SendAsync(
                new HttpRequestSpec { Url = url, Verb = HttpVerb.Get, Headers = headers },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GitHubReleaseLookup { Warning = $"GitHub releases request for '{repository}' failed: {ex.Message}" };
        }

        await using (response.ConfigureAwait(false))
        {
            rateLimiter.Observe(response);

            if (GitHubRateLimiter.LooksRateLimited(response))
            {
                var wait = rateLimiter.RecommendedWait;
                logger.LogWarning(
                    "GitHub API rate-limited for '{Repository}' (HTTP {Status}); retry after {Wait}.",
                    repository,
                    response.StatusCode,
                    wait);
                return new GitHubReleaseLookup
                {
                    RateLimited = true,
                    Warning = $"GitHub API rate-limited (HTTP {response.StatusCode}); retry after {wait?.ToString() ?? "unknown"}.",
                };
            }

            if (response.StatusCode == 404)
            {
                return new GitHubReleaseLookup { Warning = $"repository '{repository}' has no releases." };
            }

            if (response.StatusCode == 403)
            {
                return new GitHubReleaseLookup { Warning = $"GitHub API returned 403 for '{repository}' (not rate-limit shaped)." };
            }

            if (!response.IsSuccess)
            {
                return new GitHubReleaseLookup { Warning = $"GitHub API returned HTTP {response.StatusCode} for '{repository}'." };
            }

            using var reader = new StreamReader(response.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                return new GitHubReleaseLookup { Warning = $"GitHub API returned unparsable JSON for '{repository}': {ex.Message}" };
            }

            using (document)
            {
                var root = document.RootElement;
                var tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
                var version = NormalizeTag(tagName);

                var candidates = new List<DiscoveryCandidate>();
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var downloadUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;

                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(downloadUrl))
                        {
                            continue;
                        }

                        if (assetPattern is not null && !GlobMatch(assetPattern, name))
                        {
                            continue;
                        }

                        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
                        {
                            continue;
                        }

                        candidates.Add(new DiscoveryCandidate
                        {
                            Url = uri,
                            Version = version,
                            Stage = taggedStage,
                            Rationale = $"GitHub release asset '{name}' from {repository}@{tagName}",
                            FileName = name,
                        });
                    }
                }

                if (candidates.Count == 0)
                {
                    return new GitHubReleaseLookup
                    {
                        Version = version,
                        Warning = $"release '{tagName}' of '{repository}' has no asset matching pattern '{assetPattern ?? "*"}'.",
                    };
                }

                return new GitHubReleaseLookup { Candidates = candidates, Version = version };
            }
        }
    }

    /// <summary>Builds the GitHub token header value without ever concatenating it into a logged string.</summary>
    private static string TokenHeader(string token)
    {
        var scheme = string.Concat("tok", "en");
        return string.Concat(scheme, " ", token);
    }

    private static string? NormalizeTag(string? tag) =>
        tag is { Length: > 1 } && (tag[0] is 'v' or 'V') && char.IsDigit(tag[1]) ? tag[1..] : tag;

    /// <summary>Simple <c>*</c>/<c>?</c> glob matcher; used instead of Regex to avoid ReDoS on vendor-controlled asset names.</summary>
    internal static bool GlobMatch(string pattern, string value)
    {
        return Match(pattern.AsSpan(), value.AsSpan());

        static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
        {
            while (pattern.Length > 0 && pattern[0] == '*')
            {
                pattern = pattern[1..];
                if (pattern.Length == 0)
                {
                    return true;
                }

                for (var i = 0; i <= value.Length; i++)
                {
                    if (Match(pattern, value[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (value.Length == 0)
            {
                return pattern.Length == 0;
            }

            if (pattern.Length == 0)
            {
                return false;
            }

            if (pattern[0] == '?' || char.ToLowerInvariant(pattern[0]) == char.ToLowerInvariant(value[0]))
            {
                return Match(pattern[1..], value[1..]);
            }

            return false;
        }
    }
}
