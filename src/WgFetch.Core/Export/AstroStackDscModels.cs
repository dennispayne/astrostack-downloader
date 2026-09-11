using System.Text.Json.Serialization;

namespace WgFetch.Core.Export;

/// <summary>
/// The astrostack-dsc component shape as read from <c>manifest/components/*.json</c>
/// (docs/REQUIREMENTS.md, "Consumer alignment — astrostack-dsc"). Every property is optional so a
/// component missing or adding fields never fails deserialization; unrecognised JSON members are
/// silently ignored by <see cref="System.Text.Json"/> by default, which is what lets
/// <see cref="AstroStackDscImporter"/> tolerate schema drift on the astrostack-dsc side.
/// </summary>
public sealed class AstroStackDscComponent
{
    /// <summary>The lowercase slug join key, e.g. <c>nina</c>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("module")]
    public string? Module { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("versionSource")]
    public string? VersionSource { get; set; }

    [JsonPropertyName("versionStrategy")]
    public string? VersionStrategy { get; set; }

    [JsonPropertyName("versionRegex")]
    public string? VersionRegex { get; set; }

    [JsonPropertyName("checkPath")]
    public string? CheckPath { get; set; }

    /// <summary>Human-owned deliberate pin; wgfetch must never write this field (docs/REQUIREMENTS.md).</summary>
    [JsonPropertyName("expectedVersion")]
    public string? ExpectedVersion { get; set; }

    /// <summary>Machine-owned: newest version wgfetch has observed upstream but not necessarily fetched.</summary>
    [JsonPropertyName("availableVersion")]
    public string? AvailableVersion { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("downloadFileName")]
    public string? DownloadFileName { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("archiveContainsInstaller")]
    public bool? ArchiveContainsInstaller { get; set; }

    [JsonPropertyName("silentInstallArgs")]
    public string? SilentInstallArgs { get; set; }

    [JsonPropertyName("installNotes")]
    public string? InstallNotes { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("verified")]
    public bool? Verified { get; set; }
}

/// <summary>
/// A per-component export patch containing only the fields wgfetch owns
/// (docs/REQUIREMENTS.md: "wgfetch owns <c>downloadUrl</c>, <c>downloadFileName</c>, <c>sha256</c>,
/// <c>verified</c> and a new <c>availableVersion</c>"). <see cref="Id"/> is carried as the join key
/// only — every other astrostack-dsc field is intentionally absent so applying this patch can never
/// clobber a human-owned field.
/// </summary>
public sealed class AstroStackDscPatch
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("downloadFileName")]
    public string? DownloadFileName { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("verified")]
    public bool? Verified { get; set; }

    [JsonPropertyName("availableVersion")]
    public string? AvailableVersion { get; set; }
}

/// <summary>One row of the export summary: what changed and what is newly available for a component.</summary>
public sealed class AstroStackDscSummaryEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("availableVersion")]
    public string? AvailableVersion { get; set; }

    /// <summary>True when this patch carries a resolved download (state moved towards <c>acquired</c>).</summary>
    [JsonPropertyName("changed")]
    public bool Changed { get; set; }

    /// <summary>
    /// True when <see cref="AvailableVersion"/> differs from the previously known available version
    /// supplied to the exporter, so a human can decide whether to promote <c>expectedVersion</c>
    /// (docs/REQUIREMENTS.md: "do not auto-bump <c>expectedVersion</c>").
    /// </summary>
    [JsonPropertyName("newlyAvailable")]
    public bool NewlyAvailable { get; set; }
}

/// <summary>The top-level summary document written alongside the per-component patches.</summary>
public sealed class AstroStackDscSummary
{
    [JsonPropertyName("components")]
    public List<AstroStackDscSummaryEntry> Components { get; set; } = new();
}

/// <summary>
/// Source-generated, AOT-safe <c>System.Text.Json</c> context for astrostack-dsc export/import
/// payloads. Deterministic property order (declaration order) and stable indented formatting keep
/// golden-file tests reproducible.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AstroStackDscComponent))]
[JsonSerializable(typeof(AstroStackDscPatch))]
[JsonSerializable(typeof(AstroStackDscSummary))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AstroStackDscJsonContext : JsonSerializerContext
{
}
