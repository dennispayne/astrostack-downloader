using System.Text.Json;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>
/// SearXNG (docs/REQUIREMENTS.md, "Web search": "SearXNG (user-supplied instance URL)"). SearXNG
/// exposes a documented <c>/search?format=json</c> API on instances that enable it, so this queries
/// that endpoint rather than scraping the instance's HTML results page.
/// </summary>
public sealed class SearXngSearchProvider : HttpSearchProviderBase
{
    private readonly Uri _instanceBaseUrl;

    public SearXngSearchProvider(IHttpGateway http, string instanceUrl, RobotsPolicy? robots = null, HostRateLimiter? rateLimiter = null, ILogger? logger = null)
        : base(http, robots, rateLimiter, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceUrl);
        _instanceBaseUrl = new Uri(instanceUrl.TrimEnd('/') + "/");
    }

    public override string Name => "searxng";

    protected override HttpRequestSpec BuildRequest(string query, int maxResults) => new()
    {
        Url = new Uri(_instanceBaseUrl, $"search?q={Uri.EscapeDataString(query)}&format=json"),
        Verb = HttpVerb.Get,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" },
    };

    protected override IReadOnlyList<SearchResult> ParseResults(string body, int maxResults)
    {
        using var document = JsonDocument.Parse(body);
        var results = new List<SearchResult>();

        if (document.RootElement.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array)
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
                    var content = item.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    results.Add(new SearchResult(uri, title, content));
                }
            }
        }

        return results;
    }
}
