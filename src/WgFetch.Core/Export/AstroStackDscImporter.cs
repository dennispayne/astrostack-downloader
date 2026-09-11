using System.Text.Json;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Export;

/// <summary>
/// Seeds a <see cref="TargetsDocument"/> from an existing astrostack-dsc checkout's
/// <c>manifest/components/*.json</c> directory (docs/REQUIREMENTS.md, "Consumer alignment —
/// astrostack-dsc"): <c>downloadUrl: null</c> becomes <see cref="TargetState.Listed"/>, and a
/// populated <c>downloadUrl</c> with <c>verified: true</c> becomes <see cref="TargetState.Acquired"/>.
/// A populated-but-unverified <c>downloadUrl</c> becomes <see cref="TargetState.Resolved"/>: a
/// candidate is known but has not been confirmed as byte-for-byte legitimate.
/// </summary>
public sealed class AstroStackDscImporter
{
    /// <summary>
    /// Reads every <c>*.json</c> file directly under <paramref name="componentsDirectory"/> and
    /// returns a new <see cref="TargetsDocument"/> seeded from them. Files that fail to parse as JSON
    /// are skipped rather than aborting the whole import — one malformed component must not block
    /// adoption of the rest. Unknown/extra JSON fields on a component never break import (see
    /// <see cref="AstroStackDscComponent"/>).
    /// </summary>
    public async Task<TargetsDocument> ImportAsync(string componentsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentsDirectory);

        var document = new TargetsDocument();

        if (!Directory.Exists(componentsDirectory))
        {
            return document;
        }

        IEnumerable<string> files = Directory
            .EnumerateFiles(componentsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AstroStackDscComponent? component;
            try
            {
                string json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                component = JsonSerializer.Deserialize(json, AstroStackDscJsonContext.Default.AstroStackDscComponent);
            }
            catch (JsonException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(component?.Id))
            {
                continue;
            }

            TargetEntry entry = document.Add(component.Id);
            entry.ComponentId = component.Id;
            entry.AvailableVersion = component.AvailableVersion;

            if (string.IsNullOrEmpty(component.DownloadUrl))
            {
                entry.State = TargetState.Listed;
            }
            else if (component.Verified == true)
            {
                entry.State = TargetState.Acquired;
                entry.AcquiredVersion = component.ExpectedVersion;
            }
            else
            {
                entry.State = TargetState.Resolved;
            }
        }

        return document;
    }
}
