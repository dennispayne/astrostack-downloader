using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Model;
using WgFetch.Core.Verification;
using WgFetch.Core.Versioning;

namespace WgFetch.Core.Discovery;

/// <summary>
/// Runs the ordered discovery stages, first success wins, with every candidate from every stage
/// passed through <see cref="VerificationGate"/> before acceptance and every attempt — including
/// rejections and their exact reason — recorded (docs/REQUIREMENTS.md, "Discovery pipeline",
/// "The central safety invariant"). A stale recipe pointing at a dead URL logs a warning and falls
/// through to the next stage rather than aborting. Auth-walled packages set
/// <see cref="DiscoveryOutcome.RequiresAuth"/>/<see cref="DiscoveryOutcome.AuthReason"/> and write
/// nothing — the pipeline stops immediately without attempting verification.
/// </summary>
public sealed class DiscoveryPipeline
{
    private readonly IReadOnlyList<IDiscoveryStage> _stages;
    private readonly VerificationGate _gate;
    private readonly ILogger _logger;

    public DiscoveryPipeline(IReadOnlyList<IDiscoveryStage> stages, VerificationGate gate, ILogger? logger = null)
    {
        _stages = stages;
        _gate = gate;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<DiscoveryOutcome> ResolveAsync(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        var attempts = new List<CandidateVerification>();
        var sourceVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var stage in _stages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StageOutcome outcome;
            try
            {
                outcome = await stage.TryResolveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Discovery stage {Stage} threw and will be skipped: {Message}", stage.Stage, ex.Message);
                warnings.Add($"{stage.Stage}: threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (outcome.RequiresAuth)
            {
                _logger.LogInformation(
                    "'{Query}' requires authentication (P1, unsupported): {Reason}. Nothing written.",
                    request.Query,
                    outcome.AuthReason);

                return new DiscoveryOutcome
                {
                    Query = request.Query,
                    RequiresAuth = true,
                    AuthReason = outcome.AuthReason,
                    Attempts = attempts,
                    SourceVersions = sourceVersions,
                    Summary = $"'{request.Query}' requires authentication (P1, unsupported): {outcome.AuthReason}",
                };
            }

            if (outcome.Version is { Length: > 0 })
            {
                sourceVersions[stage.Stage.ToString()] = outcome.Version;
            }

            if (outcome.Warning is { Length: > 0 })
            {
                _logger.LogWarning("Discovery stage {Stage} for '{Query}': {Warning}", stage.Stage, request.Query, outcome.Warning);
                warnings.Add($"{stage.Stage}: {outcome.Warning}");
            }

            var allowlist = new DomainAllowlist(outcome.Allowlist);

            foreach (var candidate in outcome.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (request.Pin is { Length: > 0 } pin &&
                    candidate.Version is { Length: > 0 } candidateVersion &&
                    !string.Equals(candidateVersion, pin, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = await _gate.VerifyAsync(candidate.Url, allowlist, cancellationToken).ConfigureAwait(false);
                attempts.Add(new CandidateVerification(candidate, result));

                if (result.Accepted)
                {
                    return BuildSuccess(request, stage.Stage, candidate, result, attempts, sourceVersions, warnings);
                }
            }
        }

        var summary = warnings.Count > 0
            ? $"'{request.Query}' could not be resolved through any discovery stage. Warnings: {string.Join(" | ", warnings)}"
            : $"'{request.Query}' could not be resolved through any discovery stage; no stage produced a candidate.";

        _logger.LogWarning("{Summary}", summary);

        return new DiscoveryOutcome
        {
            Query = request.Query,
            Attempts = attempts,
            SourceVersions = sourceVersions,
            Summary = summary,
        };
    }

    private DiscoveryOutcome BuildSuccess(
        DiscoveryRequest request,
        DiscoveryStage acceptedStage,
        DiscoveryCandidate candidate,
        VerificationResult result,
        List<CandidateVerification> attempts,
        Dictionary<string, string> sourceVersions,
        List<string> warnings)
    {
        var acceptedVersion = candidate.Version ?? (sourceVersions.TryGetValue(acceptedStage.ToString(), out var v) ? v : null);

        if (sourceVersions.Count > 1)
        {
            var newest = VersionComparator.Instance.Newest(sourceVersions.Values.Select(VersionValue.Parse));
            if (newest is not null && acceptedVersion is not null &&
                !string.Equals(newest.Raw, acceptedVersion, StringComparison.OrdinalIgnoreCase))
            {
                var conflict =
                    $"multi-source version conflict for '{request.Query}': " +
                    string.Join(", ", sourceVersions.Select(kv => $"{kv.Key}={kv.Value}")) +
                    $"; accepted {acceptedStage}={acceptedVersion}, newest known={newest.Raw}.";
                _logger.LogWarning("{Conflict}", conflict);
                warnings.Add(conflict);
            }
        }

        Recipe? learned = acceptedStage == DiscoveryStage.LlmAssisted
            ? new Recipe
            {
                PackageId = request.KnownWingetPackageId ?? request.ComponentId,
                ComponentId = request.ComponentId,
                DisplayName = request.ComponentId,
                SourceKind = RecipeSourceKind.DirectUrl,
                Allowlist = [(result.FinalUrl ?? candidate.Url).Host],
                DirectUrl = candidate.Url.ToString(),
                Origin = RecipeOrigin.Auto,
                Notes = $"Auto-learned from a verified LLM-assisted resolution of '{request.Query}'.",
            }
            : null;

        _logger.LogInformation(
            "Resolved '{Query}' via {Stage}: {Url} (version {Version}).",
            request.Query,
            acceptedStage,
            Logging.SecretRedactor.RedactUrl(candidate.Url),
            acceptedVersion ?? "unknown");

        return new DiscoveryOutcome
        {
            Query = request.Query,
            Accepted = candidate,
            AcceptedVerification = result,
            Attempts = attempts,
            SourceVersions = sourceVersions,
            LearnedRecipe = learned,
            Summary = warnings.Count > 0
                ? $"'{request.Query}' resolved via {acceptedStage} with warnings: {string.Join(" | ", warnings)}"
                : $"'{request.Query}' resolved via {acceptedStage}.",
        };
    }
}
