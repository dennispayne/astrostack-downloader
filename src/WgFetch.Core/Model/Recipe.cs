namespace WgFetch.Core.Model;

/// <summary>Where a recipe came from; user recipes outrank seeds, which outrank auto-generated ones.</summary>
public enum RecipeOrigin
{
    /// <summary>Bundled with the binary for the astro stack.</summary>
    Seed,

    /// <summary>Supplied by the user via <c>--recipe</c> or the batch manifest.</summary>
    User,

    /// <summary>Learned from a successful verified resolution and cached for later runs.</summary>
    Auto,
}

/// <summary>How a recipe locates an installer.</summary>
public enum RecipeSourceKind
{
    /// <summary>GitHub releases API, assets filtered by pattern.</summary>
    GitHubRelease,

    /// <summary>A stable direct URL, optionally with a version placeholder.</summary>
    DirectUrl,

    /// <summary>A vendor download page plus a link-extraction rule.</summary>
    DownloadPage,

    /// <summary>An entry in a configured winget source.</summary>
    WingetSource,

    /// <summary>Recognised but not fetchable anonymously (P1).</summary>
    AuthWalled,
}

/// <summary>
/// A deterministic acquisition rule for a package. Recipes are few and frequently stale, so their
/// output is always re-validated through the verification gate (docs/REQUIREMENTS.md, "Recipes").
/// </summary>
public sealed record Recipe
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>winget PackageIdentifier, e.g. <c>AstroStack.NINA</c>.</summary>
    public required string PackageId { get; init; }

    /// <summary>Lowercase slug shared with astrostack-dsc components, e.g. <c>nina</c>.</summary>
    public required string ComponentId { get; init; }

    public required string DisplayName { get; init; }

    public string? Publisher { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public required RecipeSourceKind SourceKind { get; init; }

    /// <summary>Vendor domains this package's installer may come from.</summary>
    public IReadOnlyList<string> Allowlist { get; init; } = Array.Empty<string>();

    /// <summary><c>owner/repo</c> for <see cref="RecipeSourceKind.GitHubRelease"/>.</summary>
    public string? Repository { get; init; }

    /// <summary>Glob matched against release asset names or extracted links.</summary>
    public string? AssetPattern { get; init; }

    /// <summary>Vendor download page for <see cref="RecipeSourceKind.DownloadPage"/>.</summary>
    public string? DownloadPageUrl { get; init; }

    /// <summary>Direct installer URL; <c>{version}</c> is substituted when known.</summary>
    public string? DirectUrl { get; init; }

    /// <summary>Regex with one capture group yielding the version string.</summary>
    public string? VersionPattern { get; init; }

    /// <summary>True for packages that cannot be fetched anonymously (P1, unsupported in P0).</summary>
    public bool RequiresAuth { get; init; }

    public string? AuthReason { get; init; }

    public string? InstallerType { get; init; }

    public string? NestedInstallerType { get; init; }

    public string? Scope { get; init; }

    public string? Architecture { get; init; }

    public RecipeOrigin Origin { get; init; } = RecipeOrigin.Seed;

    public string? Notes { get; init; }
}
