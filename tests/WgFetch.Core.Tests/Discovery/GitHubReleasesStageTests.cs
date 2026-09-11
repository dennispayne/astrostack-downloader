using WgFetch.Core.Discovery;
using WgFetch.Core.Model;
using Xunit;

namespace WgFetch.Core.Tests.Discovery;

/// <summary>
/// Rate-limit and 403 handling against a fake GitHub Releases API, including <c>Retry-After</c>
/// (docs/REQUIREMENTS.md, "Testing": "rate-limit and 403 handling against a fake GitHub").
/// </summary>
public sealed class GitHubReleasesStageTests
{
    private static DiscoveryRequest Request(string repo) => new()
    {
        Query = "demo",
        ComponentId = "demo",
        KnownGitHubRepository = repo,
    };

    [Fact]
    public async Task Resolves_LatestRelease_MatchingAssetPattern()
    {
        var http = new FakeHttpGateway()
            .AddResponse(
                "https://api.github.com/repos/vendor/demo/releases/latest",
                FakeHttpResponse.Json(
                    "{\"tag_name\":\"v1.2.3\",\"assets\":[" +
                    "{\"name\":\"demo-setup.exe\",\"browser_download_url\":\"https://github.com/vendor/demo/releases/download/v1.2.3/demo-setup.exe\"}," +
                    "{\"name\":\"demo-linux.tar.gz\",\"browser_download_url\":\"https://github.com/vendor/demo/releases/download/v1.2.3/demo-linux.tar.gz\"}" +
                    "]}"));

        var stage = new GitHubReleasesStage(http, new GitHubRateLimiter(), assetPattern: "*.exe");
        var outcome = await stage.TryResolveAsync(Request("vendor/demo"), CancellationToken.None);

        Assert.Single(outcome.Candidates);
        Assert.Equal("1.2.3", outcome.Version);
        Assert.EndsWith("demo-setup.exe", outcome.Candidates[0].Url.ToString());
    }

    [Fact]
    public async Task RateLimited_403_WithZeroRemaining_ProducesWarning_NoCandidates()
    {
        var http = new FakeHttpGateway()
            .AddResponse(
                "https://api.github.com/repos/vendor/demo/releases/latest",
                new FakeHttpResponse
                {
                    StatusCode = 403,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-RateLimit-Remaining"] = "0",
                        ["X-RateLimit-Reset"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(),
                        ["Retry-After"] = "120",
                    },
                });

        var stage = new GitHubReleasesStage(http, new GitHubRateLimiter());
        var outcome = await stage.TryResolveAsync(Request("vendor/demo"), CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.Contains("rate-limited", outcome.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlainForbidden_WithoutRateLimitSignal_IsNotMisreportedAsRateLimit()
    {
        var http = new FakeHttpGateway()
            .AddResponse("https://api.github.com/repos/vendor/private/releases/latest", new FakeHttpResponse { StatusCode = 403 });

        var stage = new GitHubReleasesStage(http, new GitHubRateLimiter());
        var outcome = await stage.TryResolveAsync(Request("vendor/private"), CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.DoesNotContain("rate-limited", outcome.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not rate-limit shaped", outcome.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExhaustedRateLimiter_SkipsRequestEntirely()
    {
        var limiter = new GitHubRateLimiter();
        var http = new FakeHttpGateway()
            .AddResponse(
                "https://api.github.com/repos/vendor/demo/releases/latest",
                new FakeHttpResponse
                {
                    StatusCode = 403,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-RateLimit-Remaining"] = "0",
                        ["X-RateLimit-Reset"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(),
                    },
                });

        var stage = new GitHubReleasesStage(http, limiter);
        await stage.TryResolveAsync(Request("vendor/demo"), CancellationToken.None);
        Assert.True(limiter.IsExhausted);

        // A second call must not hit the network at all: the limiter should short-circuit it.
        var http2 = new FakeHttpGateway(); // deny-all
        var stage2 = new GitHubReleasesStage(http2, limiter);
        var outcome2 = await stage2.TryResolveAsync(Request("vendor/demo"), CancellationToken.None);

        Assert.Empty(outcome2.Candidates);
        Assert.Empty(http2.RequestedUrls);
    }

    [Fact]
    public async Task NoKnownRepository_ContributesNoCandidates_WithoutGuessing()
    {
        var http = new FakeHttpGateway();
        var stage = new GitHubReleasesStage(http, new GitHubRateLimiter());

        var outcome = await stage.TryResolveAsync(new DiscoveryRequest { Query = "demo", ComponentId = "demo" }, CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.Empty(http.RequestedUrls);
    }

    [Fact]
    public async Task NotFound_Repository_HasNoReleases_ProducesWarning()
    {
        var http = new FakeHttpGateway()
            .AddResponse("https://api.github.com/repos/vendor/empty/releases/latest", new FakeHttpResponse { StatusCode = 404 });

        var stage = new GitHubReleasesStage(http, new GitHubRateLimiter());
        var outcome = await stage.TryResolveAsync(Request("vendor/empty"), CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.Contains("no releases", outcome.Warning, StringComparison.OrdinalIgnoreCase);
    }
}
