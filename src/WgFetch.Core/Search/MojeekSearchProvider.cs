using System.Text.Json;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>
/// Mojeek Search API (<c>https://www.mojeek.com/services/search</c>), keyed via a query-string API
/// key. Mojeek is an independent, privacy-respecting index with a documented JSON API, so this never
/// scrapes Mojeek's HTML results page.
/// </summary>
public sealed class MojeekSearchProvider : HttpSearchProviderBase
{
    private readonly string _apiKey;

    public MojeekSearchProvider(IHttpGateway http, string apiKey, RobotsPolicy? robots = null, HostRateLimiter? rateLimiter = null, ILogger? logger = null)
        : base(http, robots, rateLimiter, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
    }

    public override string Name => "mojeek";

    protected override HttpRequestSpec BuildRequest(string query, int maxResults) => new()
    {
        Url = new Uri(
            $"https://www.mojeek.com/services/search?q={Uri.EscapeDataString(query)}&api_key={Uri.EscapeDataString(_apiKey)}&fmt=json&t={Math.Clamp(maxResults, 1, 20)}"),
        Verb = HttpVerb.Get,
    };

    protected override IReadOnlyList<SearchResult> ParseResults(string body, int maxResults)
    {
        using var document = JsonDocument.Parse(body);
        var results = new List<SearchResult>();

        if (document.RootElement.TryGetProperty("response", out var response) &&
            response.TryGetProperty("results", out var items) &&
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
                    var desc = item.TryGetProperty("desc", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                    results.Add(new SearchResult(uri, title, desc));
                }
            }
        }

        return results;
    }
}
