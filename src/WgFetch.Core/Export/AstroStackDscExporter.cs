using System.Text.Json;

namespace WgFetch.Core.Export;

/// <summary>
/// What wgfetch has learned about one astrostack-dsc component, ready to be turned into an export
/// patch. This is exporter input, not a persisted model: callers assemble it from whatever wgfetch
/// acquisition/discovery state is available (provenance, the winget manifest just written, etc.).
/// </summary>
public sealed record AstroStackDscExportItem
{
    /// <summary>The astrostack-dsc component slug join key, e.g. <c>nina</c>.</summary>
    public required string ComponentId { get; init; }

    public string? DownloadUrl { get; init; }

    public string? DownloadFileName { get; init; }

    public string? Sha256 { get; init; }

    public bool? Verified { get; init; }

    /// <summary>Newest version wgfetch has observed upstream, whether or not it was fetched.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>
    /// The installer's path relative to the output root (e.g. <c>installers/AstroStack.NINA/...</c>),
    /// recorded in the path-mapping file so astrostack-dsc resolves the artifact by component id
    /// instead of guessing filenames from the winget-shaped <c>installers/</c> layout
    /// (docs/REQUIREMENTS.md, "Consumer alignment — astrostack-dsc").
    /// </summary>
    public string? InstallerRelativePath { get; init; }

    /// <summary>
    /// The available version astrostack-dsc last saw for this component, if known, so the summary can
    /// flag genuinely new versions rather than repeating the same one every run.
    /// </summary>
    public string? PreviousAvailableVersion { get; init; }
}

/// <summary>
/// Emits the astrostack-dsc export format: per-component partial JSON patches containing only
/// machine-owned fields, a path-mapping file, and a change summary (docs/REQUIREMENTS.md,
/// "Consumer alignment — astrostack-dsc"). Never writes a whole-file replacement — that would
/// destroy hand-written fields such as <c>notes</c> that astrostack-dsc components depend on.
/// </summary>
public sealed class AstroStackDscExporter
{
    /// <summary>
    /// Writes <c>patches/&lt;componentId&gt;.json</c> for each item, plus <c>path-mapping.json</c> and
    /// <c>summary.json</c> under <paramref name="outputDirectory"/>. Creates the directory tree if
    /// needed. Returns the number of patches written.
    /// </summary>
    public async Task<int> ExportAsync(
        IReadOnlyList<AstroStackDscExportItem> items,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string patchesDir = Path.Combine(outputDirectory, "patches");
        Directory.CreateDirectory(patchesDir);

        var pathMapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var summary = new AstroStackDscSummary();

        // Sorted for deterministic output regardless of caller-supplied ordering.
        foreach (AstroStackDscExportItem item in items.OrderBy(i => i.ComponentId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var patch = new AstroStackDscPatch
            {
                Id = item.ComponentId,
                DownloadUrl = item.DownloadUrl,
                DownloadFileName = item.DownloadFileName,
                Sha256 = item.Sha256,
                Verified = item.Verified,
                AvailableVersion = item.AvailableVersion,
            };

            string patchPath = Path.Combine(patchesDir, $"{item.ComponentId}.json");
            await WriteJsonAsync(patchPath, patch, AstroStackDscJsonContext.Default.AstroStackDscPatch, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(item.InstallerRelativePath))
            {
                pathMapping[item.ComponentId] = item.InstallerRelativePath;
            }

            summary.Components.Add(new AstroStackDscSummaryEntry
            {
                Id = item.ComponentId,
                AvailableVersion = item.AvailableVersion,
                Changed = !string.IsNullOrEmpty(item.DownloadUrl),
                NewlyAvailable = item.AvailableVersion is not null &&
                    !string.Equals(item.AvailableVersion, item.PreviousAvailableVersion, StringComparison.Ordinal),
            });
        }

        await WriteJsonAsync(
            Path.Combine(outputDirectory, "path-mapping.json"),
            pathMapping,
            AstroStackDscJsonContext.Default.DictionaryStringString,
            cancellationToken).ConfigureAwait(false);

        await WriteJsonAsync(
            Path.Combine(outputDirectory, "summary.json"),
            summary,
            AstroStackDscJsonContext.Default.AstroStackDscSummary,
            cancellationToken).ConfigureAwait(false);

        return items.Count;
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(value, typeInfo);
        await File.WriteAllTextAsync(path, json + "\n", cancellationToken).ConfigureAwait(false);
    }
}
