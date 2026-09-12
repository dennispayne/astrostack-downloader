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
    /// document (docs/REQUIREMENTS.md: "a populated-but-unacquired repo is valid"). A file that
    /// exists but cannot be read or parsed fails closed as a <see cref="TargetsFileException"/>, so
    /// no caller has to know which parser or I/O exception type surfaced.
    /// </summary>
    public static async Task<TargetsDocument> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        (await LoadDetailedAsync(path, cancellationToken).ConfigureAwait(false)).Document;

    /// <summary>
    /// Loads <paramref name="path"/> the same way <see cref="LoadAsync"/> does, but also reports
    /// whether the file existed. Callers that must tell "nothing has ever been acquired" apart from
    /// "a targets.yaml exists but is empty" — such as the landing view's first-run hint — need this;
    /// everyone else can use <see cref="LoadAsync"/>. Reading directly instead of pre-checking with
    /// <see cref="File.Exists(string)"/> means a path that exists but cannot be inspected as a regular
    /// file — for example a directory named <c>targets.yaml</c>, or one blocked by permissions — fails
    /// closed via <see cref="TargetsFileException"/> instead of being silently treated as missing.
    /// </summary>
    internal static async Task<(TargetsDocument Document, bool Existed)> LoadDetailedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            // Probe with a read/write handle so FIFO/named-pipe paths can be classified as
            // non-regular files without risking a blocking open on the read path.
            using var probe = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            _ = RandomAccess.GetLength(probe);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing file (or a missing parent directory) is not an error: it means no targets have
            // been acquired yet.
            return (new TargetsDocument(), false);
        }
        catch (UnauthorizedAccessException)
        {
            // Read/write probing can fail on read-only files; the real read path below still decides
            // whether the file is usable.
        }
        catch (NotSupportedException ex)
        {
            throw new TargetsFileException(
                $"{path} is unreadable or malformed: path is not a regular file.",
                ex) { FilePath = path };
        }
        catch (Exception ex) when (ex is IOException)
        {
            throw new TargetsFileException($"{path} is unreadable or malformed: {ex.Message}", ex) { FilePath = path };
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing file (or a missing parent directory) is not an error: it means no targets have
            // been acquired yet.
            return (new TargetsDocument(), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TargetsFileException($"{path} is unreadable or malformed: {ex.Message}", ex) { FilePath = path };
        }

        try
        {
            return (Parse(text), true);
        }
        catch (Exception ex) when (ex is FormatException or TargetsFileException)
        {
            // A TargetsFileException from Parse already describes the parser failure, so report its
            // cause rather than nesting one "unreadable or malformed" message inside another.
            var cause = ex is TargetsFileException ? ex.InnerException ?? ex : ex;
            throw new TargetsFileException($"{path} is unreadable or malformed: {cause.Message}", cause) { FilePath = path };
        }
    }

    /// <summary>
    /// Parses <c>targets.yaml</c> content already read into memory. Parser-level failures — malformed
    /// documents, duplicate keys and other input YamlDotNet rejects — surface as
    /// <see cref="TargetsFileException"/>; schema violations surface as <see cref="FormatException"/>.
    /// </summary>
    public static TargetsDocument Parse(string yamlText)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yamlText);
            stream.Load(reader);
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        {
            // YamlDotNet reports most malformed input as YamlException, but some mapping failures
            // escape as ArgumentException from the underlying dictionary; both must fail closed.
            throw new TargetsFileException($"targets.yaml is not valid YAML: {ex.Message}", ex);
        }

        var doc = new TargetsDocument { Targets = new List<TargetEntry>() };

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return doc;
        }

        var extras = new Dictionary<string, YamlNode>(StringComparer.Ordinal);

        foreach (KeyValuePair<YamlNode, YamlNode> child in root.Children)
        {
            string key = ScalarKey(child.Key);

            if (key == VersionKey)
            {
                if (child.Value is not YamlScalarNode versionScalar)
                {
                    throw new FormatException("targets.yaml version must be a scalar.");
                }

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
            string key = ScalarKey(child.Key);
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
                    state = stateText is null ? TargetState.Listed : TargetStateExtensions.ParseYamlState(stateText);
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
                        : DateTimeOffset.Parse(
                            lastAttemptText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
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
            Name = name ?? throw new FormatException("A targets.yaml entry is missing the required 'name' field."),
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

    private static string ScalarKey(YamlNode key) =>
        key is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : throw new FormatException("targets.yaml mapping keys must be scalar.");

    /// <summary>
    /// Parses the top-level <c>version</c> scalar. Wraps <see cref="OverflowException"/> alongside
    /// the usual non-numeric case so every malformed-content path surfaces as the same
    /// <see cref="FormatException"/> callers already expect from this file.
    /// </summary>
    private static int ParseVersion(string? value)
    {
        try
        {
            return int.Parse(value ?? "1", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new FormatException($"targets.yaml 'version' must be a valid integer, got '{value}'.", ex);
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
