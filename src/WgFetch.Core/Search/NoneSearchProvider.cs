namespace WgFetch.Core.Search;

/// <summary>
/// The <c>none</c> search provider: always returns zero results. Discovery then relies solely on
/// recipes, cached auto-recipes, GitHub and winget, degrading gracefully
/// (docs/REQUIREMENTS.md, "Web search": "<c>--search-provider none</c> must be fully supported").
/// </summary>
public sealed class NoneSearchProvider : ISearchProvider
{
    public string Name => "none";

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
}
