using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace WgFetch.Core.Targets;

/// <summary>
/// Loads and saves <c>targets.yaml</c> (docs/REQUIREMENTS.md, "Target list — repo-driven
/// acquisition"). Reads and writes go through YamlDotNet's low-level representation model
/// (<see cref="YamlNode"/> and friends) rather than its attribute-driven object mapper, so that
/// keys this schema version doesn't know about are captured and re-emitted verbatim instead of
/// being silently dropped.
/// </summary>
public static class TargetsFile
{
    private const string VersionKey = "version";
    private const string TargetsKey = "targets";

    private static readonly string[] KnownEntryKeysInOrder =
    {
        "name", "id", "componentId", "state", "acquiredVersion", "availableVersion",
        "arch", "scope", "pin", "allowlist", "recipe", "lastAttempt", "lastError",
    };

    /// <summary>
    /// Loads <paramref name="path"/>. A missing file is not an error — it yields a fresh, empty
    /// document (docs/REQUIREMENTS.md: "a populated-but-unacquired repo is valid").
    /// </summary>
    public static async Task<TargetsDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new TargetsDocument();
        }

        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(text, path);
    }

    /// <summary>Parses <c>targets.yaml</c> content already read into memory.</summary>
    public static TargetsDocument Parse(string yamlText) => Parse(yamlText, null);

    private static TargetsDocument Parse(string yamlText, string? path)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yamlText);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new TargetsFileException(path, ex);
        }

        try
        {
            var doc = new TargetsDocument { Targets = new List<TargetEntry>() };

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return doc;
            }

            var extras = new Dictionary<string, YamlNode>(StringComparer.Ordinal);

            foreach (KeyValuePair<YamlNode, YamlNode> child in root.Children)
            {
                string key = GetKey(child.Key);

                if (key == VersionKey && child.Value is YamlScalarNode versionScalar)
                {
                    doc.Version = ParseVersion(versionScalar.Value);
                }
                else if (key == TargetsKey && child.Value is YamlSequenceNode targetsSequence)
                {
                    foreach (YamlNode item in targetsSequence.Children)
                    {
                        if (item is YamlMappingNode entryMapping)
                        {
                            doc.Targets.Add(ParseEntry(entryMapping));
                        }
                    }
                }
                else
                {
                    extras[key] = child.Value;
                }
            }

            doc.ExtraFields = extras;
            return doc;
        }
        catch (TargetsFileValidationException ex)
        {
            throw new TargetsFileException(path, ex.InnerException ?? ex, ex.ReasonCode);
        }
    }

    private static int ParseVersion(string? value)
    {
        try
        {
            return int.Parse(value ?? "1", CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new TargetsFileValidationException("invalid-version", ex);
        }
        catch (OverflowException ex)
        {
            throw new TargetsFileValidationException("invalid-version", ex);
        }
    }

    private static TargetEntry ParseEntry(YamlMappingNode mapping)
    {
        string? name = null;
        string? id = null;
        string? componentId = null;
        TargetState state = TargetState.Listed;
        string? acquiredVersion = null;
        string? availableVersion = null;
        string? arch = null;
        string? scope = null;
        string? pin = null;
        var allowlist = new List<string>();
        string? recipe = null;
        DateTimeOffset? lastAttempt = null;
        string? lastError = null;
        var extras = new Dictionary<string, YamlNode>(StringComparer.Ordinal);

        foreach (KeyValuePair<YamlNode, YamlNode> child in mapping.Children)
        {
            string key = GetKey(child.Key);
            YamlNode value = child.Value;

            switch (key)
            {
                case "name":
                    name = ScalarOrNull(value);
                    break;
                case "id":
                    id = ScalarOrNull(value);
                    break;
                case "componentId":
                    componentId = ScalarOrNull(value);
                    break;
                case "state":
                    string? stateText = ScalarOrNull(value);
                    state = stateText is null ? TargetState.Listed : ParseState(stateText);
                    break;
                case "acquiredVersion":
                    acquiredVersion = ScalarOrNull(value);
                    break;
                case "availableVersion":
                    availableVersion = ScalarOrNull(value);
                    break;
                case "arch":
                    arch = ScalarOrNull(value);
                    break;
                case "scope":
                    scope = ScalarOrNull(value);
                    break;
                case "pin":
                    pin = ScalarOrNull(value);
                    break;
                case "allowlist":
                    if (value is YamlSequenceNode allowlistSequence)
                    {
                        foreach (YamlNode entryNode in allowlistSequence.Children)
                        {
                            string? item = ScalarOrNull(entryNode);
                            if (item is not null)
                            {
                                allowlist.Add(item);
                            }
                        }
                    }

                    break;
                case "recipe":
                    recipe = ScalarOrNull(value);
                    break;
                case "lastAttempt":
                    string? lastAttemptText = ScalarOrNull(value);
                    lastAttempt = lastAttemptText is null
                        ? null
                        : ParseTimestamp(lastAttemptText);
                    break;
                case "lastError":
                    lastError = ScalarOrNull(value);
                    break;
                default:
                    extras[key] = value;
                    break;
            }
        }

        return new TargetEntry
        {
            Name = name ?? throw new TargetsFileValidationException(
                "missing-name",
                new FormatException("A targets.yaml entry is missing the required 'name' field.")),
            Id = id,
            ComponentId = componentId,
            State = state,
            AcquiredVersion = acquiredVersion,
            AvailableVersion = availableVersion,
            Arch = arch,
            Scope = scope,
            Pin = pin,
            Allowlist = allowlist,
            Recipe = recipe,
            LastAttempt = lastAttempt,
            LastError = lastError,
            ExtraFields = extras,
        };
    }

    private static TargetState ParseState(string value)
    {
        try
        {
            return TargetStateExtensions.ParseYamlState(value);
        }
        catch (FormatException ex)
        {
            throw new TargetsFileValidationException("invalid-state", ex);
        }
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        try
        {
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }
        catch (FormatException ex)
        {
            throw new TargetsFileValidationException("invalid-last-attempt", ex);
        }
    }

    private static string? ScalarOrNull(YamlNode node)
    {
        if (node is not YamlScalarNode scalar)
        {
            return null;
        }

        if (scalar.Style == ScalarStyle.Plain &&
            scalar.Value is null or "" or "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        return scalar.Value;
    }

    private static string GetKey(YamlNode node)
    {
        return node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : throw new TargetsFileValidationException(
                "invalid-key",
                new FormatException("targets.yaml keys must be scalar values."));
    }

    private sealed class TargetsFileValidationException(string reasonCode, Exception innerException)
        : FormatException("Invalid targets document.", innerException)
    {
        public string ReasonCode { get; } = reasonCode;
    }

    /// <summary>
    /// Saves <paramref name="document"/> to <paramref name="path"/>. The write is atomic: content is
    /// written to a temporary file in the same directory and then renamed into place, so a reader
    /// never observes a partially written <c>targets.yaml</c> (docs/REQUIREMENTS.md, "Downloads —
    /// resumable and atomic" applies the same atomic-rename discipline to shared state writes).
    /// </summary>
    public static async Task SaveAsync(TargetsDocument document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
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

    /// <summary>Renders <paramref name="document"/> to YAML text without touching disk.</summary>
    public static string Render(TargetsDocument document)
    {
        var root = new YamlMappingNode
        {
            { new YamlScalarNode(VersionKey), new YamlScalarNode(document.Version.ToString(CultureInfo.InvariantCulture)) },
        };

        var targetsSequence = new YamlSequenceNode();
        foreach (TargetEntry entry in document.Targets)
        {
            targetsSequence.Add(RenderEntry(entry));
        }

        root.Add(new YamlScalarNode(TargetsKey), targetsSequence);

        foreach (KeyValuePair<string, YamlNode> extra in document.ExtraFields)
        {
            root.Add(new YamlScalarNode(extra.Key), extra.Value);
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        string rendered = writer.ToString();

        // YamlStream.Save emits a leading "--- " document marker; targets.yaml is a single plain
        // document and reads more naturally without it.
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

    private static YamlMappingNode RenderEntry(TargetEntry entry)
    {
        var mapping = new YamlMappingNode
        {
            { "name", new YamlScalarNode(entry.Name) },
            { "id", ScalarOrNullNode(entry.Id) },
            { "componentId", ScalarOrNullNode(entry.ComponentId) },
            { "state", new YamlScalarNode(entry.State.ToYamlString()) },
            { "acquiredVersion", ScalarOrNullNode(entry.AcquiredVersion) },
            { "availableVersion", ScalarOrNullNode(entry.AvailableVersion) },
            { "arch", ScalarOrNullNode(entry.Arch) },
            { "scope", ScalarOrNullNode(entry.Scope) },
            { "pin", ScalarOrNullNode(entry.Pin) },
            { "allowlist", RenderAllowlist(entry.Allowlist) },
            { "recipe", ScalarOrNullNode(entry.Recipe) },
            { "lastAttempt", RenderTimestamp(entry.LastAttempt) },
            { "lastError", ScalarOrNullNode(entry.LastError) },
        };

        foreach (KeyValuePair<string, YamlNode> extra in entry.ExtraFields)
        {
            mapping.Add(new YamlScalarNode(extra.Key), extra.Value);
        }

        return mapping;
    }

    private static YamlNode ScalarOrNullNode(string? value) =>
        value is null ? new YamlScalarNode("null") : new YamlScalarNode(value);

    private static YamlNode RenderAllowlist(IReadOnlyList<string> allowlist)
    {
        var sequence = new YamlSequenceNode { Style = SequenceStyle.Flow };
        foreach (string item in allowlist)
        {
            sequence.Add(new YamlScalarNode(item));
        }

        return sequence;
    }

    private static YamlNode RenderTimestamp(DateTimeOffset? value)
    {
        if (value is null)
        {
            return new YamlScalarNode("null");
        }

        DateTimeOffset dto = value.Value;
        string text = dto.Offset == TimeSpan.Zero
            ? dto.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : dto.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        return new YamlScalarNode(text);
    }

    /// <summary>Fields honoured by <see cref="ParseEntry"/>, in on-disk order, for reference/tests.</summary>
    public static IReadOnlyList<string> KnownEntryFieldOrder => KnownEntryKeysInOrder;
}
