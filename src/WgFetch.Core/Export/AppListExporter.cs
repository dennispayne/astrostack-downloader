using WgFetch.Core.Targets;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace WgFetch.Core.Export;

/// <summary>
/// Emits <c>wgfetch export --format applist</c>: the plain target wishlist, stripped of local
/// acquisition state, for round-tripping into another repo's <c>targets.yaml</c> or as
/// <c>--from-file</c> batch input (docs/REQUIREMENTS.md, "Target list — repo-driven acquisition" and
/// "Configuration and inputs"). Only identity/configuration fields are kept — <c>state</c>,
/// <c>acquiredVersion</c>, <c>availableVersion</c>, <c>lastAttempt</c> and <c>lastError</c> are
/// run-local bookkeeping that would be meaningless (or misleading) in a different repo.
/// </summary>
public sealed class AppListExporter
{
    /// <summary>Renders the wishlist as YAML text.</summary>
    public string Render(TargetsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = new YamlMappingNode();
        var appsSequence = new YamlSequenceNode();

        foreach (TargetEntry entry in document.Targets)
        {
            var mapping = new YamlMappingNode
            {
                { "name", new YamlScalarNode(entry.Name) },
            };

            if (entry.Id is not null)
            {
                mapping.Add("id", new YamlScalarNode(entry.Id));
            }

            if (entry.ComponentId is not null)
            {
                mapping.Add("componentId", new YamlScalarNode(entry.ComponentId));
            }

            if (entry.Arch is not null)
            {
                mapping.Add("arch", new YamlScalarNode(entry.Arch));
            }

            if (entry.Scope is not null)
            {
                mapping.Add("scope", new YamlScalarNode(entry.Scope));
            }

            if (entry.Pin is not null)
            {
                mapping.Add("pin", new YamlScalarNode(entry.Pin));
            }

            if (entry.Allowlist.Count > 0)
            {
                var allowlist = new YamlSequenceNode { Style = SequenceStyle.Flow };
                foreach (string domain in entry.Allowlist)
                {
                    allowlist.Add(new YamlScalarNode(domain));
                }

                mapping.Add("allowlist", allowlist);
            }

            if (entry.Recipe is not null)
            {
                mapping.Add("recipe", new YamlScalarNode(entry.Recipe));
            }

            appsSequence.Add(mapping);
        }

        root.Add("apps", appsSequence);

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        string rendered = writer.ToString();
        const string marker = "--- \n";
        if (rendered.StartsWith(marker, StringComparison.Ordinal))
        {
            rendered = rendered[marker.Length..];
        }

        if (!rendered.EndsWith('\n'))
        {
            rendered += "\n";
        }

        return rendered;
    }

    /// <summary>Renders and atomically writes the wishlist to <paramref name="path"/>.</summary>
    public async Task ExportAsync(TargetsDocument document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string text = Render(document);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, text, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }
}
