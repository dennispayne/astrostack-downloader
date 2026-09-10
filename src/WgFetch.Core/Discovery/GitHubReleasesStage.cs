using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Discovery;

/// <summary>
/// Structured-upstream discovery via the GitHub Releases API, used when no recipe already resolved
/// the package (docs/REQUIREMENTS.md, "Discovery pipeline", stage 3). Contributes no candidates when
/// no repository is known — it never guesses one.
/// </summary>
public sealed class GitHubReleasesStage : IDiscoveryStage
{
    private readonly IHttpGateway _http;
    private readonly GitHubRateLimiter _rateLimiter;
    private readonly string? _githubToken;
    private readonly string? _assetPattern;
    private readonly IReadOnlyList<string> _allowlist;
    private readonly ILogger? _logger;

    public GitHubReleasesStage(
        IHttpGateway http,
        GitHubRateLimiter rateLimiter,
        string? githubToken = null,
        string? assetPattern = null,
        IReadOnlyList<string>? allowlist = null,
        ILogger? logger = null)
    {
        _http = http;
        _rateLimiter = rateLimiter;
        _githubToken = githubToken;
        _assetPattern = assetPattern;
        _allowlist = allowlist ?? ["github.com"];
        _logger = logger;
    }

    public DiscoveryStage Stage => DiscoveryStage.GitHubReleases;

    public async Task<StageOutcome> TryResolveAsync(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.KnownGitHubRepository))
        {
            return StageOutcome.Empty(Stage);
        }

        var lookup = await GitHubReleaseResolver.ResolveLatestReleaseAsync(
            _http,
            _rateLimiter,
            request.KnownGitHubRepository,
            _assetPattern,
            _githubToken,
            Stage,
            _logger,
            cancellationToken).ConfigureAwait(false);

        return new StageOutcome
        {
            Stage = Stage,
            Candidates = lookup.Candidates,
            Allowlist = _allowlist,
            Version = lookup.Version,
            Warning = lookup.Warning,
        };
    }
}
