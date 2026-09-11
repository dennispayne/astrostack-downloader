using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WgFetch.Core.Output;

/// <summary>One installer entry as shaped by the winget REST source API's <c>packageManifests</c> response.</summary>
public sealed record RestInstaller
{
    [JsonPropertyName("Architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("InstallerType")]
    public required string InstallerType { get; init; }

    [JsonPropertyName("InstallerUrl")]
    public required string InstallerUrl { get; init; }

    [JsonPropertyName("InstallerSha256")]
    public required string InstallerSha256 { get; init; }

    [JsonPropertyName("Scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("NestedInstallerType")]
    public string? NestedInstallerType { get; init; }

    [JsonPropertyName("NestedInstallerFiles")]
    public IReadOnlyList<RestNestedInstallerFile>? NestedInstallerFiles { get; init; }
}

public sealed record RestNestedInstallerFile
{
    [JsonPropertyName("RelativeFilePath")]
    public required string RelativeFilePath { get; init; }
}

public sealed record RestDefaultLocale
{
    [JsonPropertyName("PackageLocale")]
    public required string PackageLocale { get; init; }

    [JsonPropertyName("Publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("PackageName")]
    public required string PackageName { get; init; }

    [JsonPropertyName("License")]
    public required string License { get; init; }

    [JsonPropertyName("ShortDescription")]
    public string? ShortDescription { get; init; }
}

public sealed record RestPackageVersion
{
    [JsonPropertyName("PackageVersion")]
    public required string PackageVersion { get; init; }

    [JsonPropertyName("DefaultLocale")]
    public required RestDefaultLocale DefaultLocale { get; init; }

    [JsonPropertyName("Installers")]
    public required IReadOnlyList<RestInstaller> Installers { get; init; }
}

public sealed record RestPackageManifestData
{
    [JsonPropertyName("PackageIdentifier")]
    public required string PackageIdentifier { get; init; }

    [JsonPropertyName("Versions")]
    public required IReadOnlyList<RestPackageVersion> Versions { get; init; }
}

public sealed record RestPackageManifestResponse
{
    [JsonPropertyName("Data")]
    public required RestPackageManifestData Data { get; init; }
}

public sealed record RestInformationData
{
    [JsonPropertyName("SourceIdentifier")]
    public required string SourceIdentifier { get; init; }

    [JsonPropertyName("ServerSupportedVersions")]
    public required IReadOnlyList<string> ServerSupportedVersions { get; init; }
}

public sealed record RestInformationResponse
{
    [JsonPropertyName("Data")]
    public required RestInformationData Data { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RestInformationResponse))]
[JsonSerializable(typeof(RestPackageManifestResponse))]
internal sealed partial class RestJsonContext : JsonSerializerContext;

/// <summary>
/// Writes static JSON under <c>rest/</c> shaped to the winget REST source API
/// (<c>/information</c>, <c>/packageManifests/&lt;id&gt;</c>), for consumers fronting the directory
/// with a trivial HTTP server (docs/REQUIREMENTS.md, "Output layout").
/// </summary>
public sealed class RestSourceWriter
{
    public const string SourceIdentifier = "wgfetch.LocalSource";

    public static readonly IReadOnlyList<string> SupportedVersions = ["1.1.0", "1.4.0", "1.5.0", "1.6.0"];

    /// <summary>Writes <c>rest/information.json</c>.</summary>
    public async Task WriteInformationAsync(string outputRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var response = new RestInformationResponse
        {
            Data = new RestInformationData
            {
                SourceIdentifier = SourceIdentifier,
                ServerSupportedVersions = SupportedVersions,
            },
        };

        var path = SourceLayout.RestInformationPath(outputRoot);
        await WriteJsonAsync(path, response, RestJsonContext.Default.RestInformationResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes <c>rest/packageManifests/&lt;id&gt;.json</c> for one package, across all acquired versions.</summary>
    public async Task WritePackageManifestAsync(
        string outputRoot,
        string packageIdentifier,
        IReadOnlyList<RestPackageVersion> versions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageIdentifier);
        ArgumentNullException.ThrowIfNull(versions);

        var ordered = versions.OrderBy(v => v.PackageVersion, StringComparer.Ordinal).ToList();
        var response = new RestPackageManifestResponse
        {
            Data = new RestPackageManifestData
            {
                PackageIdentifier = packageIdentifier,
                Versions = ordered,
            },
        };

        var path = SourceLayout.RestPackageManifestPath(outputRoot, packageIdentifier);
        await WriteJsonAsync(path, response, RestJsonContext.Default.RestPackageManifestResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Serializing through the source-generated JsonTypeInfo<T> keeps this reflection-free and
        // AOT-safe (the library is IsAotCompatible; docs/REQUIREMENTS.md, "Prior art").
        var json = JsonSerializer.Serialize(value, typeInfo);
        await File.WriteAllTextAsync(path, json + "\n", cancellationToken).ConfigureAwait(false);
    }
}
