using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Search;

/// <summary>
/// Selects a shipped <see cref="ISearchProvider"/> by name (<c>--search-provider</c>). Never hardcodes
/// one engine as *the* implementation — every provider goes through this factory
/// (docs/REQUIREMENTS.md, "Web search").
/// </summary>
public static class SearchProviderFactory
{
    public static readonly IReadOnlyList<string> KnownNames =
    [
        "none", "duckduckgo", "brave", "startpage", "mojeek", "searxng", "google", "bing",
    ];

    public static ISearchProvider Create(
        string name,
        IHttpGateway http,
        SearchProviderOptions? options = null,
        RobotsPolicy? robots = null,
        HostRateLimiter? rateLimiter = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new SearchProviderOptions();

        return name.Trim().ToLowerInvariant() switch
        {
            "none" => new NoneSearchProvider(),
            "duckduckgo" => new DuckDuckGoSearchProvider(http, robots, rateLimiter, logger),
            "brave" => new BraveSearchProvider(http, RequireKey(name, options), robots, rateLimiter, logger),
            "mojeek" => new MojeekSearchProvider(http, RequireKey(name, options), robots, rateLimiter, logger),
            "startpage" => new StartpageSearchProvider(http, RequireEndpoint(name, options), robots, rateLimiter, logger),
            "searxng" => new SearXngSearchProvider(http, RequireEndpoint(name, options), robots, rateLimiter, logger),
            "google" => new GoogleSearchProvider(http, RequireKey(name, options), RequireEndpoint(name, options), robots, rateLimiter, logger),
            "bing" => new BingSearchProvider(http, RequireKey(name, options), robots, rateLimiter, logger),
            _ => throw new ArgumentException(
                $"Unknown search provider '{name}'. Known providers: {string.Join(", ", KnownNames)}.",
                nameof(name)),
        };
    }

    private static string RequireKey(string providerName, SearchProviderOptions options) =>
        !string.IsNullOrWhiteSpace(options.Key)
            ? options.Key
            : throw new ArgumentException($"Search provider '{providerName}' requires --search-key.");

    private static string RequireEndpoint(string providerName, SearchProviderOptions options) =>
        !string.IsNullOrWhiteSpace(options.Endpoint)
            ? options.Endpoint
            : throw new ArgumentException($"Search provider '{providerName}' requires --search-endpoint.");
}
