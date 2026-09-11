namespace WgFetch.Core.Search;

/// <summary>A single ranked web search result (docs/REQUIREMENTS.md, "Web search").</summary>
public sealed record SearchResult(Uri Url, string Title, string Snippet);

/// <summary>
/// Pluggable web search. Adding a provider requires implementing this interface and nothing else
/// (docs/REQUIREMENTS.md, "Web search — pluggable, no hardcoded engine").
/// </summary>
public interface ISearchProvider
{
    /// <summary>Name used to select this provider via <c>--search-provider</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// Returns ranked results for a query. Implementations must go through <see cref="Abstractions.IHttpGateway"/>,
    /// honour <see cref="RobotsPolicy"/> and rate-limit per host — never scrape a search engine's HTML
    /// when an API is offered.
    /// </summary>
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken);
}
