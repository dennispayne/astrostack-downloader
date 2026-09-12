using System.Text.Json;
using System.Text.Json.Serialization;

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
    public WgFetchConfig Redacted() => this with
    {
        AiEndpoint = AiEndpoint is null ? null : Logging.SecretRedactor.Redact(AiEndpoint),
        SearchEndpoint = SearchEndpoint is null ? null : Logging.SecretRedactor.Redact(SearchEndpoint),
        AiKey = string.IsNullOrEmpty(AiKey) ? AiKey : Logging.SecretRedactor.Placeholder,
        SearchKey = string.IsNullOrEmpty(SearchKey) ? SearchKey : Logging.SecretRedactor.Placeholder,
        GithubToken = string.IsNullOrEmpty(GithubToken) ? GithubToken : Logging.SecretRedactor.Placeholder,
    };
}

/// <summary>Loads and saves <see cref="WgFetchConfig"/>; a missing or malformed file is never fatal.</summary>
public static class ConfigFile
{
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer
                .SerializeAsync(stream, config, ConfigJsonContext.Default.WgFetchConfig, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
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
