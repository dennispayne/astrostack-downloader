using System.Text.Json;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>Bing Web Search API, keyed via the <c>Ocp-Apim-Subscription-Key</c> header.</summary>
public sealed class BingSearchProvider : HttpSearchProviderBase
{
    private readonly string _apiKey;

    public BingSearchProvider(IHttpGateway http, string apiKey, RobotsPolicy? robots = null, HostRateLimiter? rateLimiter = null, ILogger? logger = null)
        : base(http, robots, rateLimiter, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
    }

    public override string Name => "bing";

    protected override HttpRequestSpec BuildRequest(string query, int maxResults) => new()
    {
        Url = new Uri($"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={Math.Clamp(maxResults, 1, 20)}"),
        Verb = HttpVerb.Get,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Ocp-Apim-Subscription-Key"] = _apiKey },
    };

    protected override IReadOnlyList<SearchResult> ParseResults(string body, int maxResults)
    {
        using var document = JsonDocument.Parse(body);
        var results = new List<SearchResult>();

        if (document.RootElement.TryGetProperty("webPages", out var webPages) &&
            webPages.TryGetProperty("value", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                if (item.TryGetProperty("url", out var urlProp) &&
                    urlProp.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(urlProp.GetString(), UriKind.Absolute, out var uri))
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                    results.Add(new SearchResult(uri, name, snippet));
                }
            }
        }

        return results;
    }
}
