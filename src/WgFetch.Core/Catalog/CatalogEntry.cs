namespace WgFetch.Core.Catalog;

/// <summary>
/// A resolvable package: identifier, display name, publisher, tags and aliases, embedded eagerly
/// since the catalog is small and recipe-driven, not a 10k-package mirror
/// (docs/REQUIREMENTS.md, "Name resolution").
/// </summary>
public sealed record CatalogEntry
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Publisher { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>The text embedded for this entry: display name, publisher, tags and aliases joined.</summary>
    public string EmbeddingText() =>
        string.Join(
            " ",
            new[] { DisplayName, Publisher }
                .Concat(Tags)
                .Concat(Aliases)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
}
