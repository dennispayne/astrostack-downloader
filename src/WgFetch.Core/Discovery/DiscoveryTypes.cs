using WgFetch.Core.Model;

namespace WgFetch.Core.Discovery;

/// <summary>Input to the discovery pipeline for one package (docs/REQUIREMENTS.md, "Discovery pipeline").</summary>
public sealed record DiscoveryRequest
{
    /// <summary>The friendly query resolved to this package (for provenance/logging only).</summary>
    public required string Query { get; init; }

    public required string ComponentId { get; init; }

    /// <summary>A recipe explicitly supplied by the user for this run, if any (highest trust).</summary>
    public Recipe? UserRecipe { get; init; }

    /// <summary>The bundled seed recipe for this component, if any.</summary>
    public Recipe? SeedRecipe { get; init; }

    /// <summary>A previously learned auto-recipe for this component, if any.</summary>
    public Recipe? AutoRecipe { get; init; }

    public string? Architecture { get; init; }

    public string? Scope { get; init; }

    /// <summary>An exact version pin; when set, discovery only accepts a candidate at this version.</summary>
    public string? Pin { get; init; }

    /// <summary>
    /// <c>owner/repo</c> to try in <see cref="GitHubReleasesStage"/> when no recipe already supplied
    /// one. Left null when unknown; the stage then contributes no candidates rather than guessing.
    /// </summary>
    public string? KnownGitHubRepository { get; init; }

    /// <summary>winget <c>PackageIdentifier</c> (e.g. <c>AstroStack.NINA</c>) to try in <see cref="WingetStage"/>.</summary>
    public string? KnownWingetPackageId { get; init; }
}

/// <summary>
/// What one <see cref="IDiscoveryStage"/> found: zero or more untrusted candidate URLs to be run
/// through <see cref="Verification.VerificationGate"/> by the pipeline, never by the stage itself.
/// </summary>
public sealed record StageOutcome
{
    public static StageOutcome Empty(DiscoveryStage stage, string? warning = null) => new()
    {
        Stage = stage,
        Warning = warning,
    };

    public required DiscoveryStage Stage { get; init; }

    public IReadOnlyList<DiscoveryCandidate> Candidates { get; init; } = Array.Empty<DiscoveryCandidate>();

    /// <summary>The domains this stage's candidates may legitimately come from.</summary>
    public IReadOnlyList<string> Allowlist { get; init; } = Array.Empty<string>();

    /// <summary>The version this stage believes is current, for multi-source conflict recording.</summary>
    public string? Version { get; init; }

    public bool RequiresAuth { get; init; }

    public string? AuthReason { get; init; }

    /// <summary>A non-fatal issue with this stage (e.g. a stale recipe's URL is dead); logged, pipeline continues.</summary>
    public string? Warning { get; init; }

    /// <summary>Set by <see cref="LlmAssistedStage"/> to seed the auto-recipe cache after a verified success.</summary>
    public Recipe? LearnedRecipeTemplate { get; init; }
}

/// <summary>One stage of the ordered discovery pipeline (docs/REQUIREMENTS.md, "Discovery pipeline").</summary>
public interface IDiscoveryStage
{
    DiscoveryStage Stage { get; }

    Task<StageOutcome> TryResolveAsync(DiscoveryRequest request, CancellationToken cancellationToken);
}
