using System.Text.Json;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>
/// DuckDuckGo via its Instant Answer API (<c>api.duckduckgo.com/?format=json</c>) — an API, not HTML
/// scraping, and usable with no account. The privacy-respecting shipped default
/// (docs/REQUIREMENTS.md, "Web search").
/// </summary>
public sealed class DuckDuckGoSearchProvider : HttpSearchProviderBase
{
    public DuckDuckGoSearchProvider(IHttpGateway http, RobotsPolicy? robots = null, HostRateLimiter? rateLimiter = null, ILogger? logger = null)
        : base(http, robots, rateLimiter, logger)
    {
    }

    public override string Name => "duckduckgo";

    protected override HttpRequestSpec BuildRequest(string query, int maxResults) => new()
    {
        Url = new Uri(
            $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&no_redirect=1&skip_disambig=1"),
        Verb = HttpVerb.Get,
    };

    protected override IReadOnlyList<SearchResult> ParseResults(string body, int maxResults)
    {
        using var document = JsonDocument.Parse(body);
        var results = new List<SearchResult>();

        if (document.RootElement.TryGetProperty("AbstractURL", out var abstractUrl) &&
            abstractUrl.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(abstractUrl.GetString(), UriKind.Absolute, out var absUri))
        {
            var heading = document.RootElement.TryGetProperty("Heading", out var h) ? h.GetString() ?? string.Empty : string.Empty;
            var abstractText = document.RootElement.TryGetProperty("AbstractText", out var a) ? a.GetString() ?? string.Empty : string.Empty;
            results.Add(new SearchResult(absUri, heading, abstractText));
        }

        if (document.RootElement.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
        {
            CollectTopics(topics, results, maxResults);
        }

        return results.Take(maxResults).ToArray();
    }

    private static void CollectTopics(JsonElement topics, List<SearchResult> results, int maxResults)
    {
        foreach (var topic in topics.EnumerateArray())
        {
            if (results.Count >= maxResults)
            {
                return;
            }

            if (topic.TryGetProperty("Topics", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                CollectTopics(nested, results, maxResults);
                continue;
            }

            if (topic.TryGetProperty("FirstURL", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(urlProp.GetString(), UriKind.Absolute, out var uri))
            {
                var text = topic.TryGetProperty("Text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                results.Add(new SearchResult(uri, text, text));
            }
        }
    }
}
