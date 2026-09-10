using WgFetch.Core.Verification;

namespace WgFetch.Core.Model;

/// <summary>Ordered discovery stages; the first success wins (docs/REQUIREMENTS.md, "Discovery pipeline").</summary>
public enum DiscoveryStage
{
    UserRecipe,
    SeedRecipe,
    AutoRecipe,
    GitHubReleases,
    WingetSource,
    LlmAssisted,
}

/// <summary>A proposed installer URL. Untrusted until it passes the verification gate.</summary>
public sealed record DiscoveryCandidate
{
    public required Uri Url { get; init; }

    public string? Version { get; init; }

    public required DiscoveryStage Stage { get; init; }

    /// <summary>Where the candidate came from, e.g. a release asset name or a page link text.</summary>
    public string? Rationale { get; init; }

    /// <summary>Vendor-published hash accompanying the candidate, if any.</summary>
    public string? UpstreamSha256 { get; init; }

    public string? FileName { get; init; }
}

/// <summary>A candidate paired with the mechanical verdict of the verification gate.</summary>
public sealed record CandidateVerification(DiscoveryCandidate Candidate, VerificationResult Result);

/// <summary>The outcome of running the discovery pipeline for one package.</summary>
public sealed record DiscoveryOutcome
{
    public required string Query { get; init; }

    public Recipe? Recipe { get; init; }

    public DiscoveryCandidate? Accepted { get; init; }

    public VerificationResult? AcceptedVerification { get; init; }

    public IReadOnlyList<CandidateVerification> Attempts { get; init; } = Array.Empty<CandidateVerification>();

    /// <summary>Versions reported by other sources, for the multi-source conflict warning.</summary>
    public IReadOnlyDictionary<string, string> SourceVersions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool RequiresAuth { get; init; }

    public string? AuthReason { get; init; }

    /// <summary>An auto-generated recipe learned from a successful LLM-assisted resolution.</summary>
    public Recipe? LearnedRecipe { get; init; }

    public required string Summary { get; init; }

    public bool Success => Accepted is not null && AcceptedVerification?.Accepted == true;
}
