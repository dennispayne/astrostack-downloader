using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
    /// everyone else can use <see cref="LoadAsync"/>. A path that exists but is not a readable regular
    /// file — a directory named <c>targets.yaml</c>, a FIFO, or one blocked by permissions — fails
    /// closed via <see cref="TargetsFileException"/> instead of being silently treated as missing or
    /// blocking the caller.
    /// </summary>
    internal static async Task<(TargetsDocument Document, bool Existed)> LoadDetailedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string text;
        try
        {
            text = OperatingSystem.IsLinux()
                ? await LinuxRegularFileReader.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
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
    /// Reads a Linux path without ever blocking on a special file. The path is opened once with
    /// <c>O_NONBLOCK</c> — so opening a FIFO whose writer never arrives returns immediately instead of
    /// waiting — and every subsequent decision is made about that one open description, so a path
    /// swapped between the check and the read cannot smuggle a FIFO past the check. When the kernel or
    /// libc cannot answer "is this a regular file?" the read still proceeds against the non-blocking
    /// handle, which fails closed with an I/O error rather than hanging.
    /// </summary>
    private static class LinuxRegularFileReader
    {
        private const int ReadOnly = 0x0000;
        private const int NonBlocking = 0x0800;
        private const int CloseOnExec = 0x80000;

        private const int NoSuchFileOrDirectory = 2;
        private const int NotADirectory = 20;
        private const int PermissionDenied = 13;
        private const int IsADirectory = 21;

        private const int ChunkBytes = 64 * 1024;
        private const int MaxBytes = 8 * 1024 * 1024;

        internal static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            SafeFileHandle handle;
            try
            {
                handle = Open(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No usable libc entry point: fall back to the portable managed read. Special files stay
                // possible here, but so does every platform this code was never able to inspect.
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }

            using (handle)
            {
                EnsureRegularFile(handle);

                await using var stream = new FileStream(handle, FileAccess.Read);
                return await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads at most <see cref="MaxBytes"/>. A file type this build could not identify — no
        /// <c>statx</c>, or a kernel that refused it — must still not be able to feed an endless stream
        /// such as <c>/dev/zero</c> into memory, so the cap, not the type check, is what bounds the read.
        /// </summary>
        private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var content = new MemoryStream();
            var chunk = new byte[ChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > MaxBytes)
                {
                    throw new IOException($"file is larger than the {MaxBytes / (1024 * 1024)} MiB targets.yaml limit.");
                }

                content.Write(chunk, 0, read);
            }

            content.Position = 0;
            using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        private static SafeFileHandle Open(string path)
        {
            var utf8Path = new byte[Encoding.UTF8.GetByteCount(path) + 1];
            Encoding.UTF8.GetBytes(path, utf8Path);

            var descriptor = OpenNative(utf8Path, ReadOnly | NonBlocking | CloseOnExec);
            if (descriptor >= 0)
            {
                return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            throw error switch
            {
                NoSuchFileOrDirectory or NotADirectory => new FileNotFoundException(null, path),
                PermissionDenied => new UnauthorizedAccessException($"Access to '{path}' is denied."),
                IsADirectory => new IOException("path is not a regular file."),
                _ => new IOException($"unable to open the file (errno {error})."),
            };
        }

        private static void EnsureRegularFile(SafeFileHandle handle)
        {
            if (LinuxFileType.TryGetIsRegular(handle, out var isRegular) && !isRegular)
            {
                throw new IOException("path is not a regular file.");
            }
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenNative(byte[] path, int flags);
    }

    /// <summary>
    /// Answers "is this open file description a regular file?" using <c>statx</c> against the handle
    /// itself (<c>AT_EMPTY_PATH</c>), never the path, so the answer cannot be invalidated by a rename.
    /// Kernels and libc versions without <c>statx</c> report "unknown" instead of failing: callers must
    /// stay safe without an answer.
    /// </summary>
    private static class LinuxFileType
    {
        private const int AtEmptyPath = 0x1000;
        private const uint FileTypeMaskRequest = 1;
        private const ushort FileTypeMask = 0xF000;
        private const ushort RegularFile = 0x8000;

        private static readonly byte[] EmptyPath = [0];

        internal static bool TryGetIsRegular(SafeFileHandle handle, out bool isRegular)
        {
            isRegular = false;
            var referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                if (Statx((int)handle.DangerousGetHandle(), EmptyPath, AtEmptyPath, FileTypeMaskRequest, out var stat) != 0)
                {
                    return false;
                }

                isRegular = (stat.Mode & FileTypeMask) == RegularFile;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                if (referenced)
                {
                    handle.DangerousRelease();
                }
            }
        }

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int Statx(int directoryFileDescriptor, byte[] path, int flags, uint mask, out LinuxStatx stat);

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxStatx
        {
            [FieldOffset(28)]
            internal ushort Mode;
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
