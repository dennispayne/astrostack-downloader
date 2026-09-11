using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Model;

namespace WgFetch.Core.Discovery;

/// <summary>
/// Base implementation shared by <see cref="UserRecipeStage"/>, <see cref="SeedRecipeStage"/> and
/// <see cref="AutoRecipeStage"/>: given a <see cref="Recipe"/>, produces untrusted candidates
/// according to its <see cref="RecipeSourceKind"/>. Recipes are few and frequently stale, so a dead
/// URL here is never fatal — the pipeline logs the stage's warning and falls through
/// (docs/REQUIREMENTS.md, "Discovery pipeline", stage 1).
/// </summary>
public abstract class RecipeBackedStage : IDiscoveryStage
{
    private readonly IHttpGateway _http;
    private readonly ILogger _logger;

    protected RecipeBackedStage(IHttpGateway http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger.Instance;
    }

    public abstract DiscoveryStage Stage { get; }

    /// <summary>Selects the recipe this stage considers from the request (user/seed/auto).</summary>
    protected abstract Recipe? SelectRecipe(DiscoveryRequest request);

    public async Task<StageOutcome> TryResolveAsync(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        var recipe = SelectRecipe(request);
        if (recipe is null)
        {
            return StageOutcome.Empty(Stage);
        }

        if (recipe.RequiresAuth || recipe.SourceKind == RecipeSourceKind.AuthWalled)
        {
            return new StageOutcome
            {
                Stage = Stage,
                Allowlist = recipe.Allowlist,
                RequiresAuth = true,
                AuthReason = recipe.AuthReason ?? "requires authentication (P1, unsupported)",
            };
        }

        return recipe.SourceKind switch
        {
            RecipeSourceKind.DirectUrl => ResolveDirectUrl(recipe, request),
            RecipeSourceKind.GitHubRelease => await ResolveGitHubReleaseAsync(recipe, cancellationToken).ConfigureAwait(false),
            RecipeSourceKind.DownloadPage => await ResolveDownloadPageAsync(recipe, cancellationToken).ConfigureAwait(false),
            RecipeSourceKind.WingetSource => await WingetStage.ResolveFromRecipeAsync(_http, recipe, Stage, _logger, cancellationToken).ConfigureAwait(false),
            _ => StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' has unsupported source kind '{recipe.SourceKind}'."),
        };
    }

    private StageOutcome ResolveDirectUrl(Recipe recipe, DiscoveryRequest request)
    {
        if (string.IsNullOrWhiteSpace(recipe.DirectUrl))
        {
            return StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' has SourceKind.DirectUrl but no DirectUrl.");
        }

        var resolvedUrl = recipe.DirectUrl.Replace("{version}", request.Pin ?? string.Empty, StringComparison.Ordinal);

        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri))
        {
            return StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' DirectUrl '{recipe.DirectUrl}' is not a valid absolute URL.");
        }

        // A pin wins; failing that the version is read out of the URL itself, since a direct-URL recipe
        // has no other place to carry one and an unversioned acquisition cannot be retained or compared.
        var version = request.Pin ?? ExtractVersion(recipe.VersionPattern, resolvedUrl);

        return new StageOutcome
        {
            Stage = Stage,
            Allowlist = recipe.Allowlist,
            Version = version,
            Candidates =
            [
                new DiscoveryCandidate
                {
                    Url = uri,
                    Version = version,
                    Stage = Stage,
                    Rationale = $"recipe '{recipe.ComponentId}' direct URL",
                    FileName = Path.GetFileName(uri.LocalPath),
                },
            ],
        };
    }

    /// <summary>Applies a recipe's version pattern to <paramref name="text"/>, preferring a named group.</summary>
    private static string? ExtractVersion(string? pattern, string text)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var match = SafeMatch(pattern, text);
        if (match is not { Success: true })
        {
            return null;
        }

        var named = match.Groups["version"];
        if (named.Success)
        {
            return named.Value;
        }

        return match.Groups.Count > 1 ? match.Groups[1].Value : null;
    }

    private async Task<StageOutcome> ResolveGitHubReleaseAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipe.Repository))
        {
            return StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' has SourceKind.GitHubRelease but no Repository.");
        }

        var lookup = await GitHubReleaseResolver.ResolveLatestReleaseAsync(
            _http,
            SharedRateLimiter,
            recipe.Repository,
            recipe.AssetPattern,
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
            Stage,
            _logger,
            cancellationToken).ConfigureAwait(false);

        return new StageOutcome
        {
            Stage = Stage,
            Allowlist = recipe.Allowlist,
            Candidates = lookup.Candidates,
            Version = lookup.Version,
            Warning = lookup.Warning,
        };
    }

    private async Task<StageOutcome> ResolveDownloadPageAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipe.DownloadPageUrl) || !Uri.TryCreate(recipe.DownloadPageUrl, UriKind.Absolute, out var pageUrl))
        {
            return StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' has SourceKind.DownloadPage but no usable DownloadPageUrl.");
        }

        HttpResponseSpec response;
        try
        {
            response = await _http.SendAsync(
                new HttpRequestSpec { Url = pageUrl, Verb = HttpVerb.Get },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' download page '{recipe.DownloadPageUrl}' unreachable: {ex.Message}");
        }

        await using (response.ConfigureAwait(false))
        {
            if (!response.IsSuccess)
            {
                return StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' download page returned HTTP {response.StatusCode}.");
            }

            using var reader = new StreamReader(response.Body);
            var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var reduced = HtmlReducer.Reduce(html, maxTextLength: 20_000, maxLinks: 500);

            var version = ExtractVersion(recipe.VersionPattern, reduced.Text);

            var allowlist = new Verification.DomainAllowlist(recipe.Allowlist);
            var candidates = new List<DiscoveryCandidate>();
            foreach (var link in reduced.Links)
            {
                if (!LooksLikeInstallerLink(link.Href))
                {
                    continue;
                }

                if (!Uri.TryCreate(pageUrl, link.Href, out var resolved))
                {
                    continue;
                }

                if (!allowlist.Allows(resolved))
                {
                    continue;
                }

                candidates.Add(new DiscoveryCandidate
                {
                    Url = resolved,
                    Version = version,
                    Stage = Stage,
                    Rationale = $"recipe '{recipe.ComponentId}' download page link '{link.Text}'",
                    FileName = Path.GetFileName(resolved.LocalPath),
                });
            }

            if (candidates.Count == 0)
            {
                return StageOutcome.Empty(Stage, $"recipe '{recipe.ComponentId}' download page had no allowlisted installer-like link.");
            }

            return new StageOutcome
            {
                Stage = Stage,
                Allowlist = recipe.Allowlist,
                Candidates = candidates,
                Version = version,
            };
        }
    }

    private static Match? SafeMatch(string pattern, string input)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return regex.Match(input);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool LooksLikeInstallerLink(string href)
    {
        var withoutQuery = href.Split('?', 2)[0];
        return withoutQuery.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               withoutQuery.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
               withoutQuery.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               withoutQuery.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A process-wide rate limiter shared by every recipe-backed stage that resolves a GitHub release,
    /// so a burst across many recipes still accounts against one ceiling.
    /// </summary>
    internal static readonly GitHubRateLimiter SharedRateLimiter = new();
}
