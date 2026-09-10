namespace WgFetch.Core.Search;

/// <summary>
/// Configuration for a search provider: <c>--search-endpoint</c> (self-hosted instance, e.g. SearXNG)
/// and <c>--search-key</c> (API-keyed providers). The key is never logged directly — callers must
/// route any diagnostic rendering of this record through <see cref="Logging.SecretRedactor"/>.
/// </summary>
public sealed record SearchProviderOptions
{
    public string? Endpoint { get; init; }

    public string? Key { get; init; }
}
