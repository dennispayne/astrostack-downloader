using Microsoft.Extensions.Logging;
using WgFetch.Core.Catalog;
using WgFetch.Core.Discovery;
using WgFetch.Core.Inference;
using WgFetch.Core.Logging;
using WgFetch.Core.Model;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Recipes;
using WgFetch.Core.Search;

namespace WgFetch.Core.Cli;

/// <summary>
/// Name resolution and pipeline construction: the two-tier resolver, the inference router and the
/// ordered discovery stages (docs/REQUIREMENTS.md, "Name resolution", "Discovery pipeline").
/// </summary>
public sealed partial class CommandRunner
{
    /// <summary>Inference wiring for one run, or the reason there is none.</summary>
    private sealed record ResolverContext
    {
        public NameResolver? Resolver { get; init; }

        public IInferenceRouter? Router { get; init; }

        /// <summary>Set when a prerequisite is missing and the run cannot continue.</summary>
        public ExitCode? ExitCode { get; init; }

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>How a friendly name became a recipe, recorded in provenance.</summary>
    private sealed record NameResolution
    {
        public Recipe? Recipe { get; init; }

        public required string Tier { get; init; }

        public double? Confidence { get; init; }

        public FetchOutcome? Failure { get; init; }
    }

    /// <summary>
    /// Builds the resolver only when it is actually needed. Embedding weights are a hard prerequisite
    /// for fuzzy resolution, but an exact name/alias hit against a recipe needs no model at all, so a
    /// missing model is not fatal until a query genuinely requires inference.
    /// </summary>
    private async Task<ResolverContext> CreateResolverAsync(
        RecipeStore recipes,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        var router = CreateRouter(settings);

        if (_dependencies.Embeddings is { } injected)
        {
            var resolver = new NameResolver(BuildCatalog(recipes), injected, _dependencies.LocalGenerator);
            await resolver.WarmAsync(cancellationToken).ConfigureAwait(false);
            return new ResolverContext { Resolver = resolver, Router = router };
        }

        var statuses = await PrereqInstaller.StatusAsync(settings.ModelsRoot, cancellationToken).ConfigureAwait(false);
        var embedding = statuses.FirstOrDefault(s => s.ModelId == PinnedModels.EmbeddingModelId);
        if (embedding?.Ready == true)
        {
            _logger.LogWarning(
                "Embedding weights are present at {Directory} but no ONNX runtime backend is wired into this build; " +
                "falling back to exact recipe matching.",
                embedding.Directory);
        }

        // No embeddings: exact matching still works, so carry on and fail per-name if inference is needed.
        return new ResolverContext { Router = router };
    }

    private IInferenceRouter? CreateRouter(RunSettings settings)
    {
        var local = _dependencies.LocalGenerator;
        var remote = _dependencies.RemoteGenerator ?? CreateRemoteGenerator(settings);

        if (local is null && remote is null)
        {
            return null;
        }

        // InferenceRouter requires a local generator; with only a remote one configured, it *is* the local.
        return new InferenceRouter(
            local ?? remote!,
            local is null ? null : remote,
            local is null ? AiMode.Local : settings.AiMode,
            localTimeout: null,
            remoteEndpointForLogging: settings.AiEndpoint,
            logger: _logger);
    }

    private ITextGenerator? CreateRemoteGenerator(RunSettings settings)
    {
        if (settings.AiMode == AiMode.Local)
        {
            return null;
        }

        if (settings.AiEndpoint is not { Length: > 0 } endpoint || settings.AiModel is not { Length: > 0 } model)
        {
            return null;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("Ignoring --ai-endpoint: it must be an absolute https URL.");
            return null;
        }

        return new RemoteOpenAiTextGenerator(new HttpOpenAiChatClient(), uri, model, settings.AiKey, _logger);
    }

    private async Task<NameResolution> ResolveNameAsync(
        string name,
        RecipeStore recipes,
        ResolverContext context,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        // Tier 0: an exact package id, component id, alias or tag needs no inference whatsoever.
        if (recipes.Find(name) is { } exact)
        {
            return new NameResolution { Recipe = exact, Tier = "exact", Confidence = 1.0 };
        }

        if (context.Resolver is null)
        {
            var message =
                $"'{name}' does not match any recipe by name, id, alias or tag, and fuzzy resolution is " +
                $"unavailable. {PrereqInstaller.MissingModelMessage(PinnedModels.Embedding)}";

            return new NameResolution
            {
                Tier = "unavailable",
                Failure = new FetchOutcome
                {
                    Query = name,
                    ExitCode = ExitCode.MissingPrerequisite,
                    Message = message,
                },
            };
        }

        using var scope = _timings.Measure("resolve");
        var result = await context.Resolver
            .ResolveAsync(name, settings.Threshold, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsAmbiguous || result.Best is null)
        {
            var ranked = string.Join(
                ", ",
                result.Candidates.Take(5).Select(c => $"{c.Entry.Id} ({c.Confidence:F2})"));

            var message = result.Candidates.Count == 0
                ? $"'{name}' matched nothing in the catalog."
                : $"'{name}' is ambiguous. Candidates: {ranked}. Re-run with an exact id or raise --threshold.";

            return new NameResolution
            {
                Tier = result.UsedTier2 ? "tier2" : "tier1",
                Confidence = result.Best?.Confidence,
                Failure = new FetchOutcome
                {
                    Query = name,
                    ExitCode = result.Candidates.Count == 0 ? ExitCode.Unresolved : ExitCode.Ambiguous,
                    Message = message,
                },
            };
        }

        var recipe = recipes.Find(result.Best.Entry.Id);
        if (recipe is null)
        {
            return new NameResolution
            {
                Tier = "tier1",
                Failure = new FetchOutcome
                {
                    Query = name,
                    ExitCode = ExitCode.Unresolved,
                    Message = $"'{name}' resolved to '{result.Best.Entry.Id}' but no recipe backs it.",
                },
            };
        }

        return new NameResolution
        {
            Recipe = recipe,
            Tier = result.UsedTier2 ? "tier2" : "tier1",
            Confidence = result.Best.Confidence,
        };
    }

    /// <summary>
    /// The ordered stage list. The LLM-assisted stage is included only when a generator exists; every
    /// stage proposes candidates that the pipeline — never the stage — runs through the gate.
    /// </summary>
    private IReadOnlyList<IDiscoveryStage> BuildStages(RunSettings settings, IInferenceRouter? router)
    {
        var http = CreateHttpGateway();
        var stages = new List<IDiscoveryStage>
        {
            new UserRecipeStage(http, _logger),
            new SeedRecipeStage(http, _logger),
            new AutoRecipeStage(http, _logger),
            new GitHubReleasesStage(http, new GitHubRateLimiter(), settings.GithubToken, logger: _logger),
            new WingetStage(http, _logger),
        };

        if (router is not null)
        {
            var search = SearchProviderFactory.Create(
                settings.SearchProvider,
                http,
                new SearchProviderOptions { Endpoint = settings.SearchEndpoint, Key = settings.SearchKey },
                new RobotsPolicy(http),
                new HostRateLimiter(),
                _logger);

            stages.Add(new LlmAssistedStage(search, http, router, _logger));
        }
        else
        {
            _logger.LogInformation(
                "No text generator is available, so LLM-assisted discovery is disabled for this run. {Message}",
                PrereqInstaller.MissingModelMessage(PinnedModels.LanguageModel));
        }

        return stages;
    }
}
