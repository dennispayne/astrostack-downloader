using System.Text.Json;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>Google Programmable Search JSON API, keyed via <c>--search-key</c> (and a configured CSE id passed as the endpoint's <c>cx</c>).</summary>
public sealed class GoogleSearchProvider : HttpSearchProviderBase
{
    private readonly string _apiKey;
    private readonly string _searchEngineId;

    public GoogleSearchProvider(IHttpGateway http, string apiKey, string searchEngineId, RobotsPolicy? robots = null, HostRateLimiter? rateLimiter = null, ILogger? logger = null)
        : base(http, robots, rateLimiter, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchEngineId);
        _apiKey = apiKey;
        _searchEngineId = searchEngineId;
    }

    public override string Name => "google";

    protected override HttpRequestSpec BuildRequest(string query, int maxResults) => new()
    {
        Url = new Uri(
            $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(_apiKey)}&cx={Uri.EscapeDataString(_searchEngineId)}&q={Uri.EscapeDataString(query)}&num={Math.Clamp(maxResults, 1, 10)}"),
        Verb = HttpVerb.Get,
    };

    protected override IReadOnlyList<SearchResult> ParseResults(string body, int maxResults)
    {
        using var document = JsonDocument.Parse(body);
        var results = new List<SearchResult>();

        if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                if (item.TryGetProperty("link", out var linkProp) &&
                    linkProp.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(linkProp.GetString(), UriKind.Absolute, out var uri))
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                    results.Add(new SearchResult(uri, title, snippet));
                }
            }
        }

        return results;
    }
}
