using System.Text.Json;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>
/// Brave Search API (<c>api.search.brave.com/res/v1/web/search</c>), keyed via
/// <c>X-Subscription-Token</c>. Requires <c>--search-key</c>.
/// </summary>
public sealed class BraveSearchProvider : HttpSearchProviderBase
{
    private readonly string _apiKey;

    public BraveSearchProvider(IHttpGateway http, string apiKey, RobotsPolicy? robots = null, HostRateLimiter? rateLimiter = null, ILogger? logger = null)
        : base(http, robots, rateLimiter, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
    }

    public override string Name => "brave";

    protected override HttpRequestSpec BuildRequest(string query, int maxResults) => new()
    {
        Url = new Uri($"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={Math.Clamp(maxResults, 1, 20)}"),
        Verb = HttpVerb.Get,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Subscription-Token"] = _apiKey,
            ["Accept"] = "application/json",
        },
    };

    protected override IReadOnlyList<SearchResult> ParseResults(string body, int maxResults)
    {
        using var document = JsonDocument.Parse(body);
        var results = new List<SearchResult>();

        if (document.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var items) &&
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
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                    results.Add(new SearchResult(uri, title, description));
                }
            }
        }

        return results;
    }
}
