namespace WgFetch.Core.Output;

/// <summary>Silent/interactive install switches, recorded verbatim from the upstream manifest or recipe.</summary>
public sealed record InstallerSwitches
{
    public string? Silent { get; init; }

    public string? SilentWithProgress { get; init; }

    public bool IsEmpty => Silent is null && SilentWithProgress is null;
}

/// <summary>
/// One installer within a manifest's installer set. For a ZIP bundle, <see cref="NestedInstallerType"/>
/// and <see cref="NestedInstallerFiles"/> record what is inside without ever extracting the archive
/// (docs/REQUIREMENTS.md, "Do not extract archives.").
/// </summary>
public sealed record WingetInstallerEntry
{
    public required string Architecture { get; init; }

    public required string InstallerType { get; init; }

    /// <summary>Local path/URI the installer was rewritten to point at. Never the original vendor URL.</summary>
    public required Uri InstallerUrl { get; init; }

    /// <summary>Locally computed SHA256 of the downloaded bytes, lowercase hex.</summary>
    public required string InstallerSha256 { get; init; }

    public string? Scope { get; init; }

    public InstallerSwitches? Switches { get; init; }

    /// <summary>Installer type inside a ZIP bundle, e.g. <c>exe</c>. Only set for ZIP/MSIX containers.</summary>
    public string? NestedInstallerType { get; init; }

    /// <summary>Relative paths of nested installer files inside the (untouched) archive.</summary>
    public IReadOnlyList<string> NestedInstallerFiles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Everything needed to emit the winget-pkgs three-file manifest set for one package version
/// (docs/REQUIREMENTS.md, "Output layout").
/// </summary>
public sealed record WingetManifestRequest
{
    public required string PackageIdentifier { get; init; }

    public required string PackageVersion { get; init; }

    public required string Publisher { get; init; }

    public required string PackageName { get; init; }

    public string DefaultLocale { get; init; } = "en-US";

    public string License { get; init; } = "Proprietary";

    public string? ShortDescription { get; init; }

    public string? PublisherUrl { get; init; }

    public string? PackageUrl { get; init; }

    public required IReadOnlyList<WingetInstallerEntry> Installers { get; init; }
}

/// <summary>Outcome of a manifest write attempt. Failure never leaves partial files on disk.</summary>
public sealed record ManifestWriteResult
{
    public required bool Success { get; init; }

    public required string Reason { get; init; }

    public string? VersionManifestPath { get; init; }

    public string? InstallerManifestPath { get; init; }

    public string? LocaleManifestPath { get; init; }
}
