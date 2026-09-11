namespace WgFetch.Core.Catalog;

/// <summary>A catalog entry ranked against a query, with its cosine-similarity confidence.</summary>
public sealed record ResolutionCandidate(CatalogEntry Entry, double Confidence);

/// <summary>
/// Outcome of resolving a fuzzy name against the catalog (docs/REQUIREMENTS.md, "Name resolution").
/// Ambiguity — top-1 confidence below <c>--threshold</c>, or top candidates clustered too closely
/// together — is a caller-visible result, not an exception: print ranked candidates and exit
/// non-zero when not attached to a TTY.
/// </summary>
public sealed record ResolutionResult
{
    public required string Query { get; init; }

    public IReadOnlyList<ResolutionCandidate> Candidates { get; init; } = Array.Empty<ResolutionCandidate>();

    public bool UsedTier2 { get; init; }

    public required bool IsAmbiguous { get; init; }

    public ResolutionCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
}
