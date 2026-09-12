using System.Text.Json;
using System.Text.Json.Serialization;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Configuration;

/// <summary>
/// Persisted user settings. Precedence is CLI flag &gt; environment variable &gt; config file &gt;
/// built-in default (docs/REQUIREMENTS.md, "Configuration and inputs").
/// </summary>
public sealed record WgFetchConfig
{
    [JsonPropertyName("outputDirectory")]
    public string? OutputDirectory { get; init; }

    [JsonPropertyName("cacheDirectory")]
    public string? CacheDirectory { get; init; }

    [JsonPropertyName("modelsRoot")]
    public string? ModelsRoot { get; init; }

    [JsonPropertyName("threshold")]
    public double? Threshold { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("keepVersions")]
    public int? KeepVersions { get; init; }

    [JsonPropertyName("aiMode")]
    public string? AiMode { get; init; }

    [JsonPropertyName("aiEndpoint")]
    public string? AiEndpoint { get; init; }

    [JsonPropertyName("aiModel")]
    public string? AiModel { get; init; }

    [JsonPropertyName("aiKey")]
    public string? AiKey { get; init; }

    [JsonPropertyName("searchProvider")]
    public string? SearchProvider { get; init; }

    [JsonPropertyName("searchEndpoint")]
    public string? SearchEndpoint { get; init; }

    [JsonPropertyName("searchKey")]
    public string? SearchKey { get; init; }

    [JsonPropertyName("githubToken")]
    public string? GithubToken { get; init; }

    [JsonPropertyName("logLevel")]
    public string? LogLevel { get; init; }

    [JsonPropertyName("parallelDownloads")]
    public int? ParallelDownloads { get; init; }

    [JsonPropertyName("maxPerHost")]
    public int? MaxPerHost { get; init; }

    [JsonPropertyName("plain")]
    public bool? Plain { get; init; }

    /// <summary>Secret values held by this config, for redaction of arbitrary output.</summary>
    [JsonIgnore]
    public IEnumerable<string> Secrets
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AiKey))
            {
                yield return AiKey;
            }

            if (!string.IsNullOrWhiteSpace(SearchKey))
            {
                yield return SearchKey;
            }

            if (!string.IsNullOrWhiteSpace(GithubToken))
            {
                yield return GithubToken;
            }
        }
    }

    /// <summary>
    /// Returns a copy with every secret replaced, for diagnostics bundles, <c>config get</c> and
    /// <c>config list</c>. Beyond the three dedicated credential fields, free-text values such as
    /// <c>aiEndpoint</c>/<c>searchEndpoint</c> can themselves embed a token or basic-auth userinfo
    /// (for example <c>******host/...</c> or <c>?api_key=...</c>), so they are run through
    /// the same <see cref="Logging.SecretRedactor"/> used for logs (docs/REQUIREMENTS.md, "Privacy").
    /// </summary>
    /// <param name="supplementalSecrets">
    /// Additional effective secrets to redact beyond this config's own <see cref="Secrets"/>, such as
    /// CLI/environment credentials from <c>RunSettings.Secrets</c> that are not persisted here but may
    /// still be embedded in a persisted endpoint value.
    /// </param>
    public WgFetchConfig Redacted(IEnumerable<string>? supplementalSecrets = null)
    {
        var secrets = Secrets
            .Concat(supplementalSecrets ?? [])
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string? Redact(string? value)
        {
            if (value is null)
            {
                return null;
            }

            var redacted = value;
            foreach (var secret in secrets)
            {
                redacted = redacted.Replace(secret, Logging.SecretRedactor.Placeholder, StringComparison.Ordinal);
                redacted = redacted.Replace(
                    Uri.EscapeDataString(secret),
                    Logging.SecretRedactor.Placeholder,
                    StringComparison.Ordinal);
            }

            return Logging.SecretRedactor.Redact(redacted, secrets);
        }

        // Endpoints are URLs that may carry a configured credential as a query value or userinfo,
        // percent- or form-encoded (+ for space) differently than Uri.EscapeDataString would produce
        // above; decode and compare query values before falling back to the generic string redaction.
        // The URL-aware pass is followed by the generic redaction so token-shaped values that are not
        // configured secrets (e.g. an "sk-" key in the path) are redacted too.
        string? RedactEndpoint(string? value) =>
            value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile
                ? Redact(Logging.SecretRedactor.RedactUrl(value, secrets))
                : Redact(value);

        return this with
        {
            OutputDirectory = Redact(OutputDirectory),
            CacheDirectory = Redact(CacheDirectory),
            ModelsRoot = Redact(ModelsRoot),
            Architecture = Redact(Architecture),
            Scope = Redact(Scope),
            AiMode = Redact(AiMode),
            AiEndpoint = RedactEndpoint(AiEndpoint),
            AiModel = Redact(AiModel),
            AiKey = string.IsNullOrEmpty(AiKey) ? AiKey : Logging.SecretRedactor.Placeholder,
            SearchProvider = Redact(SearchProvider),
            SearchEndpoint = RedactEndpoint(SearchEndpoint),
            SearchKey = string.IsNullOrEmpty(SearchKey) ? SearchKey : Logging.SecretRedactor.Placeholder,
            GithubToken = string.IsNullOrEmpty(GithubToken) ? GithubToken : Logging.SecretRedactor.Placeholder,
            LogLevel = Redact(LogLevel),
        };
    }
}

/// <summary>Loads and saves <see cref="WgFetchConfig"/>; a missing or malformed file is never fatal.</summary>
public static class ConfigFile
{
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    public static string DefaultPath => Path.Combine(WgFetchPaths.RootDirectory, "config.json");

    public static async Task<WgFetchConfig> LoadAsync(string? path, CancellationToken cancellationToken)
    {
        var target = path ?? DefaultPath;
        if (!File.Exists(target))
        {
            return new WgFetchConfig();
        }

        await using var stream = File.OpenRead(target);
        try
        {
            return await JsonSerializer
                .DeserializeAsync(stream, ConfigJsonContext.Default.WgFetchConfig, cancellationToken)
                .ConfigureAwait(false) ?? new WgFetchConfig();
        }
        catch (JsonException)
        {
            return new WgFetchConfig();
        }
    }

    public static async Task SaveAsync(WgFetchConfig config, string path, CancellationToken cancellationToken)
    {
        await using var transactionLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        await SaveUnlockedAsync(config, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically applies a read-modify-write update. Returns <see langword="null"/> when
    /// <paramref name="update"/> rejects the current config, but throws <see cref="InvalidDataException"/>
    /// when an existing config file is malformed so the caller never overwrites it with defaults.
    /// </summary>
    public static async Task<WgFetchConfig?> TryUpdateAsync(
        string path,
        Func<WgFetchConfig, WgFetchConfig?> update,
        CancellationToken cancellationToken)
    {
        await using var transactionLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        var current = await LoadForUpdateAsync(path, cancellationToken).ConfigureAwait(false);
        var updated = update(current);
        if (updated is null)
        {
            return null;
        }

        await SaveUnlockedAsync(updated, path, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Reads the config for a read-modify-write cycle. Unlike <see cref="LoadAsync"/>, which is
    /// deliberately lenient, only a genuinely missing file yields defaults: an existing file we fail to
    /// read (for example an ACL or sharing failure, which <see cref="File.Exists"/> also reports as
    /// missing) propagates instead of being replaced by the caller's write, and malformed JSON is
    /// surfaced instead of being collapsed to defaults, either of which would otherwise silently
    /// discard persisted settings and credentials.
    /// </summary>
    private static async Task<WgFetchConfig> LoadForUpdateAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = File.OpenRead(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new WgFetchConfig();
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await JsonSerializer
                    .DeserializeAsync(stream, ConfigJsonContext.Default.WgFetchConfig, cancellationToken)
                    .ConfigureAwait(false) ?? new WgFetchConfig();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Existing config file '{path}' is malformed; fix or remove it before changing settings.",
                    exception);
            }
        }
    }

    private static async Task SaveUnlockedAsync(
        WgFetchConfig config,
        string path,
        CancellationToken cancellationToken)
    {
        EnsureProtectedDirectory(path);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous,
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temp, options))
            {
                await JsonSerializer
                    .SerializeAsync(stream, config, ConfigJsonContext.Default.WgFetchConfig, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureProtectedDirectory(fullPath);
        var lockPath = fullPath + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception) when (FileLockContention.IsContention(exception))
            {
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureProtectedDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        // The default directory is dedicated to wgfetch credentials and is always private. A caller
        // may place --config in an existing shared directory, whose permissions we must not rewrite;
        // the config file itself is still replaced with owner-only permissions.
        if (!OperatingSystem.IsWindows() &&
            (!existed || string.Equals(fullPath, Path.GetFullPath(DefaultPath), StringComparison.Ordinal)))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Default on-disk locations (docs/REQUIREMENTS.md, "Prerequisites — frictionless first run").</summary>
public static class WgFetchPaths
{
    public static string RootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "wgfetch");

    public static string ModelsDirectory => Path.Combine(RootDirectory, "models");

    public static string SourceDirectory => Path.Combine(RootDirectory, "source");

    public static string CacheDirectory => Path.Combine(RootDirectory, "cache");
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WgFetchConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
