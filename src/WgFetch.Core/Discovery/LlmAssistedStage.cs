using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Inference;
using WgFetch.Core.Model;
using WgFetch.Core.Search;

namespace WgFetch.Core.Discovery;

/// <summary>
/// LLM-assisted discovery — the expected primary path for ~95% of fetches
/// (docs/REQUIREMENTS.md, "Purpose and framing", "Discovery pipeline" stage 4). Locates the vendor's
/// download page via the configured <see cref="ISearchProvider"/>, fetches and reduces the HTML, has
/// the model propose candidate links and a version string, and returns them as
/// <em>untrusted candidates</em> for the pipeline to run through
/// <see cref="Verification.VerificationGate"/>. This stage never verifies anything itself and never
/// widens the allowlist it was given — the model's output can only be reduced further (via the
/// verification gate) than the seeded allowlist, never expanded, so a hallucinated or prompt-injected
/// domain is mechanically rejected regardless of what the model claims.
/// </summary>
public sealed class LlmAssistedStage : IDiscoveryStage
{
    private readonly ISearchProvider _search;
    private readonly IHttpGateway _http;
    private readonly IInferenceRouter _router;
    private readonly ILogger _logger;

    public LlmAssistedStage(ISearchProvider search, IHttpGateway http, IInferenceRouter router, ILogger? logger = null)
    {
        _search = search;
        _http = http;
        _router = router;
        _logger = logger ?? NullLogger.Instance;
    }

    public DiscoveryStage Stage => DiscoveryStage.LlmAssisted;

    public async Task<StageOutcome> TryResolveAsync(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        var allowlist = request.UserRecipe?.Allowlist
            ?? request.SeedRecipe?.Allowlist
            ?? request.AutoRecipe?.Allowlist
            ?? Array.Empty<string>();

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await _search.SearchAsync($"{request.Query} download installer", 5, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StageOutcome.Empty(Stage, $"search provider failed: {ex.Message}");
        }

        if (results.Count == 0)
        {
            return StageOutcome.Empty(Stage, "search provider returned no results (or --search-provider none).");
        }

        var pageUrl = SelectPage(results, allowlist);
        var reduced = await FetchAndReduceAsync(pageUrl, cancellationToken).ConfigureAwait(false);
        if (reduced is null)
        {
            return StageOutcome.Empty(Stage, $"could not fetch or reduce candidate page '{pageUrl}'.");
        }

        var prompt = BuildPrompt(request.Query, pageUrl, reduced);

        string completion;
        try
        {
            completion = await _router.GenerateAsync(prompt, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("LLM-assisted discovery: generation failed: {Message}", ex.Message);
            return StageOutcome.Empty(Stage, $"model generation failed: {ex.Message}");
        }

        var proposals = ParseProposals(completion);
        if (proposals.Count == 0)
        {
            _logger.LogWarning("LLM-assisted discovery: model output was empty, malformed or contained no usable URL for '{Query}'.", request.Query);
            return StageOutcome.Empty(Stage, "model proposed no parsable candidate URLs; failing closed.");
        }

        var candidates = proposals
            .Where(p => Uri.TryCreate(p.Url, UriKind.Absolute, out _))
            .Select(p => new DiscoveryCandidate
            {
                Url = new Uri(p.Url),
                Version = p.Version,
                Stage = Stage,
                Rationale = $"LLM-proposed from page '{pageUrl}'",
            })
            .ToArray();

        if (candidates.Length == 0)
        {
            return StageOutcome.Empty(Stage, "model proposed URLs but none were well-formed absolute URLs; failing closed.");
        }

        return new StageOutcome
        {
            Stage = Stage,
            Allowlist = allowlist,
            Candidates = candidates,
            Version = proposals[0].Version,
        };
    }

    private static Uri SelectPage(IReadOnlyList<SearchResult> results, IReadOnlyList<string> allowlist)
    {
        if (allowlist.Count > 0)
        {
            var allowed = new Verification.DomainAllowlist(allowlist);
            var match = results.FirstOrDefault(r => allowed.Allows(r.Url));
            if (match is not null)
            {
                return match.Url;
            }
        }

        return results[0].Url;
    }

    private async Task<ReducedPage?> FetchAndReduceAsync(Uri pageUrl, CancellationToken cancellationToken)
    {
        if (!string.Equals(pageUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var response = await _http.SendAsync(new HttpRequestSpec { Url = pageUrl, Verb = HttpVerb.Get }, cancellationToken).ConfigureAwait(false);
            await using (response.ConfigureAwait(false))
            {
                if (!response.IsSuccess)
                {
                    return null;
                }

                using var reader = new StreamReader(response.Body);
                var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return HtmlReducer.Reduce(html);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPrompt(string query, Uri pageUrl, ReducedPage reduced)
    {
        return
            $"Find the current download link for \"{query}\" on the page below ({pageUrl}). " +
            "Reply with ONLY a JSON object of the shape " +
            "{\"candidates\":[{\"url\":\"https://...\",\"version\":\"1.2.3\"}]} " +
            "listing URLs that literally appear as hrefs in the data below. No prose, no markdown fences.\n\n" +
            HtmlReducer.WrapAsUntrustedData(reduced);
    }

    internal sealed record Proposal(string Url, string? Version);

    /// <summary>
    /// Parses the model's proposed-candidates JSON. Malformed, empty, truncated or prompt-injected
    /// (e.g. non-JSON prose trying to smuggle instructions) output yields an empty list rather than
    /// throwing — the caller fails closed (docs/REQUIREMENTS.md, "Testing": "LLM returns malformed...
    /// output → fails closed").
    /// </summary>
    internal static IReadOnlyList<Proposal> ParseProposals(string? completion)
    {
        if (string.IsNullOrWhiteSpace(completion))
        {
            return Array.Empty<Proposal>();
        }

        var json = ExtractJsonObject(completion);
        if (json is null)
        {
            return Array.Empty<Proposal>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var results = new List<Proposal>();

            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in candidates.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var url = item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    var version = item.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        results.Add(new Proposal(url, version));
                    }
                }
            }
            else if (root.TryGetProperty("url", out var singleUrl) && singleUrl.ValueKind == JsonValueKind.String)
            {
                var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var urlText = singleUrl.GetString();
                if (!string.IsNullOrWhiteSpace(urlText))
                {
                    results.Add(new Proposal(urlText, version));
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<Proposal>();
        }
    }

    /// <summary>Extracts the first balanced <c>{...}</c> object from arbitrary model output, tolerating surrounding prose/fences.</summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        // Unbalanced braces: truncated output. Fail closed rather than guessing.
        return null;
    }
}
