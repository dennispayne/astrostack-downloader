using WgFetch.Core.Inference;

namespace WgFetch.Core.Catalog;

/// <summary>
/// Two-tier fuzzy name resolution (docs/REQUIREMENTS.md, "Name resolution").
/// <para>
/// <b>Tier 1 — embeddings.</b> Catalog entries are embedded eagerly (warm) with the E5 <c>"passage: "</c>
/// prefix; the query is embedded at request time with the <c>"query: "</c> prefix. Cosine similarity
/// over the warm catalog is the target &lt;100ms operation.
/// </para>
/// <para>
/// <b>Tier 2 — Phi-3.5-mini</b> (via the injected <see cref="ITextGenerator"/>), invoked only when the
/// top Tier-1 confidence is below <c>--threshold</c> or the top candidates are too closely clustered.
/// A malformed, empty or unresolved Tier-2 completion fails closed: the caller still gets the ranked
/// Tier-1 candidates, marked ambiguous.
/// </para>
/// </summary>
public sealed class NameResolver
{
    private readonly IReadOnlyList<CatalogEntry> _catalog;
    private readonly IEmbeddingModel _embeddings;
    private readonly ITextGenerator? _tier2;
    private readonly double _clusterMargin;
    private float[][]? _catalogVectors;

    public NameResolver(
        IReadOnlyList<CatalogEntry> catalog,
        IEmbeddingModel embeddings,
        ITextGenerator? tier2 = null,
        double clusterMargin = 0.03)
    {
        _catalog = catalog;
        _embeddings = embeddings;
        _tier2 = tier2;
        _clusterMargin = clusterMargin;
    }

    /// <summary>Eagerly embeds the whole catalog so Tier-1 lookups are warm (docs/REQUIREMENTS.md, "Name resolution").</summary>
    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        var texts = _catalog.Select(e => "passage: " + e.EmbeddingText()).ToArray();
        _catalogVectors = _catalog.Count == 0
            ? Array.Empty<float[]>()
            : await _embeddings.EmbedAsync(texts, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResolutionResult> ResolveAsync(string query, double threshold, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (_catalogVectors is null)
        {
            await WarmAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_catalog.Count == 0)
        {
            return new ResolutionResult { Query = query, Candidates = Array.Empty<ResolutionCandidate>(), IsAmbiguous = true };
        }

        var queryVector = (await _embeddings.EmbedAsync([$"query: {query}"], cancellationToken).ConfigureAwait(false))[0];

        var ranked = _catalog
            .Select((entry, i) => new ResolutionCandidate(entry, CosineSimilarity(queryVector, _catalogVectors![i])))
            .OrderByDescending(c => c.Confidence)
            .ToList();

        var clustered = ranked.Count >= 2 && ranked[0].Confidence - ranked[1].Confidence < _clusterMargin;
        var belowThreshold = ranked[0].Confidence < threshold;

        if (!belowThreshold && !clustered)
        {
            return new ResolutionResult { Query = query, Candidates = ranked, IsAmbiguous = false };
        }

        if (_tier2 is null)
        {
            return new ResolutionResult { Query = query, Candidates = ranked, IsAmbiguous = true };
        }

        return await ResolveTier2Async(query, ranked, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolutionResult> ResolveTier2Async(
        string query,
        List<ResolutionCandidate> ranked,
        CancellationToken cancellationToken)
    {
        string completion;
        try
        {
            var prompt = BuildPrompt(query, ranked);
            completion = await _tier2!.GenerateAsync(prompt, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fails closed: Tier-2 unavailable or erroring surfaces Tier-1 candidates as ambiguous.
            return new ResolutionResult { Query = query, Candidates = ranked, UsedTier2 = true, IsAmbiguous = true };
        }

        var chosenId = ExtractChosenId(completion);
        var chosen = chosenId is null
            ? null
            : ranked.FirstOrDefault(c => string.Equals(c.Entry.Id, chosenId, StringComparison.OrdinalIgnoreCase));

        if (chosen is null)
        {
            // Malformed, empty or unresolved completion: fail closed, keep Tier-1 order, stay ambiguous.
            return new ResolutionResult { Query = query, Candidates = ranked, UsedTier2 = true, IsAmbiguous = true };
        }

        var reordered = new List<ResolutionCandidate> { chosen };
        reordered.AddRange(ranked.Where(c => !ReferenceEquals(c, chosen)));
        return new ResolutionResult { Query = query, Candidates = reordered, UsedTier2 = true, IsAmbiguous = false };
    }

    private static string BuildPrompt(string query, IReadOnlyList<ResolutionCandidate> ranked)
    {
        var options = string.Join(
            "\n",
            ranked.Take(5).Select(c => $"- id={c.Entry.Id} name=\"{c.Entry.DisplayName}\" confidence={c.Confidence:F3}"));

        return
            "You are disambiguating a fuzzy application name against a fixed catalog. " +
            "Reply with exactly one line: \"CHOSEN_ID: <id>\" using one of the ids listed below, or " +
            "\"CHOSEN_ID: none\" if none match.\n" +
            $"Query: \"{query}\"\n" +
            $"Candidates:\n{options}";
    }

    /// <summary>Extracts the <c>CHOSEN_ID:</c> line. Returns null for malformed, empty or "none" output.</summary>
    internal static string? ExtractChosenId(string? completion)
    {
        if (string.IsNullOrWhiteSpace(completion))
        {
            return null;
        }

        foreach (var line in completion.Split('\n'))
        {
            var trimmed = line.Trim();
            const string marker = "CHOSEN_ID:";
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[marker.Length..].Trim();
                if (value.Length == 0 || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return value;
            }
        }

        return null;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
        }

        // Vectors are already L2-normalized by IEmbeddingModel implementations, so the dot product
        // alone is the cosine similarity; no additional magnitude division is needed.
        return dot;
    }
}
