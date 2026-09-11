using System.Text.Json;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>
/// Startpage does not publish a general-purpose public search API, so this provider talks to a
/// user-supplied Startpage-compatible JSON proxy endpoint (<c>--search-endpoint</c>), the same shape
/// used by <see cref="SearXngSearchProvider"/>. This still satisfies "never scrape a search engine's
/// HTML" because the shipped code path never parses Startpage's HTML results page — it is the
/// operator's responsibility to point <c>--search-endpoint</c> at a JSON-emitting proxy they control.
/// </summary>
public sealed class StartpageSearchProvider : HttpSearchProviderBase
{
    private readonly Uri _endpoint;

    public StartpageSearchProvider(IHttpGateway http, string endpointUrl, RobotsPolicy? robots = null, HostRateLimiter? rateLimiter = null, ILogger? logger = null)
        : base(http, robots, rateLimiter, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        _endpoint = new Uri(endpointUrl);
    }

    public override string Name => "startpage";

    protected override HttpRequestSpec BuildRequest(string query, int maxResults) => new()
    {
        Url = new Uri(_endpoint, $"?q={Uri.EscapeDataString(query)}&format=json"),
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
                    var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                    results.Add(new SearchResult(uri, title, snippet));
                }
            }
        }

        return results;
    }
}
