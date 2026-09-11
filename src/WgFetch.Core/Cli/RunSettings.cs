using Microsoft.Extensions.Logging;
using WgFetch.Core.Configuration;
using WgFetch.Core.Inference;
using WgFetch.Core.Logging;
using WgFetch.Core.Output;

namespace WgFetch.Core.Cli;

/// <summary>
/// Effective settings for one run. Precedence is strictly
/// <b>CLI flag &gt; environment variable &gt; config file &gt; built-in default</b>
/// (docs/REQUIREMENTS.md, "Configuration and inputs").
/// </summary>
public sealed record RunSettings
{
    public required string OutputDirectory { get; init; }

    public required string CacheDirectory { get; init; }

    public required string ModelsRoot { get; init; }

    public string? InstallerDirectory { get; init; }

    public string? FromFile { get; init; }

    public string? RecipePath { get; init; }

    public string? RecipeInline { get; init; }

    public string Architecture { get; init; } = "x64";

    public string Scope { get; init; } = "machine";

    public double Threshold { get; init; } = 0.62;

    public int KeepVersions { get; init; } = 2;

    public bool RequireHashMatch { get; init; }

    public AiMode AiMode { get; init; } = AiMode.Local;

    public string? AiEndpoint { get; init; }

    public string? AiModel { get; init; }

    public string? AiKey { get; init; }

    public string? GithubToken { get; init; }

    public int ParallelDownloads { get; init; } = 3;

    public int MaxPerHost { get; init; } = 2;

    public string SearchProvider { get; init; } = "duckduckgo";

    public string? SearchEndpoint { get; init; }

    public string? SearchKey { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    public string? LogFile { get; init; }

    public bool Json { get; init; }

    public bool Plain { get; init; }

    public bool NoColor { get; init; }

    public bool DryRun { get; init; }

    public bool NoResume { get; init; }

    public bool DownloadPrereqs { get; init; }

    public bool OnlyMissing { get; init; }

    public bool RefreshStale { get; init; }

    public string? OutDirectory { get; init; }

    public string? Format { get; init; }

    /// <summary>Secret values that must never appear in any output.</summary>
    public IReadOnlyList<string> Secrets =>
        new[] { AiKey, SearchKey, GithubToken }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

    public static RunSettings Resolve(
        ParsedCommandLine parsed,
        WgFetchConfig config,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(config);

        // --winget-repo names an existing source tree and is simply another way of saying --output.
        var output = First(
            parsed.Value("--output"),
            parsed.Value("--winget-repo"),
            Env("WGFETCH_OUTPUT"),
            config.OutputDirectory,
            WgFetchPaths.SourceDirectory);

        return new RunSettings
        {
            OutputDirectory = Path.GetFullPath(output!),
            CacheDirectory = Path.GetFullPath(First(
                parsed.Value("--cache-dir"),
                Env("WGFETCH_CACHE"),
                config.CacheDirectory,
                WgFetchPaths.CacheDirectory)!),
            ModelsRoot = Path.GetFullPath(First(
                parsed.Value("--models-root"),
                parsed.Value("--models-dir"),
                Env("WGFETCH_MODELS"),
                config.ModelsRoot,
                WgFetchPaths.ModelsDirectory)!),
            InstallerDirectory = parsed.Value("--installer-dir"),
            FromFile = parsed.Value("--from-file"),
            RecipePath = parsed.Value("--recipe"),
            RecipeInline = parsed.Value("--recipe-inline"),
            Architecture = First(parsed.Value("--arch"), config.Architecture, HostArchitecture())!,
            Scope = First(parsed.Value("--scope"), config.Scope, "machine")!,
            Threshold = parsed.DoubleValue("--threshold") ?? config.Threshold ?? 0.62,
            KeepVersions = parsed.IntValue("--keep-versions") ?? config.KeepVersions ?? 2,
            RequireHashMatch = parsed.Has("--require-hash-match"),
            AiMode = ParseAiMode(First(parsed.Value("--ai-mode"), config.AiMode, "local")!),
            AiEndpoint = First(parsed.Value("--ai-endpoint"), Env("WGFETCH_AI_ENDPOINT"), config.AiEndpoint),
            AiModel = First(parsed.Value("--ai-model"), config.AiModel),
            AiKey = First(parsed.Value("--ai-key"), Env("WGFETCH_AI_KEY"), config.AiKey),
            GithubToken = First(parsed.Value("--github-token"), Env("GITHUB_TOKEN"), config.GithubToken),
            ParallelDownloads = Clamp(
                parsed.IntValue("--parallel-downloads") ?? config.ParallelDownloads ?? 3,
                1,
                16),
            MaxPerHost = Clamp(parsed.IntValue("--max-per-host") ?? config.MaxPerHost ?? 2, 1, 8),
            SearchProvider = First(parsed.Value("--search-provider"), config.SearchProvider, "duckduckgo")!,
            SearchEndpoint = First(parsed.Value("--search-endpoint"), config.SearchEndpoint),
            SearchKey = First(parsed.Value("--search-key"), Env("WGFETCH_SEARCH_KEY"), config.SearchKey),
            LogLevel = LogLevelParser.Parse(First(parsed.Value("--log-level"), config.LogLevel)),
            LogFile = parsed.Value("--log-file"),
            Json = parsed.Has("--json"),
            Plain = parsed.Has("--plain") || config.Plain == true,
            NoColor = parsed.Has("--no-color"),
            DryRun = parsed.Has("--dry-run"),
            NoResume = parsed.Has("--no-resume"),
            DownloadPrereqs = parsed.Has("--download-prereqs"),
            OnlyMissing = parsed.Has("--only-missing"),
            RefreshStale = parsed.Has("--refresh-stale"),
            OutDirectory = parsed.Value("--out"),
            Format = parsed.Value("--format"),
        };

        string? Env(string name) => environment is null
            ? Environment.GetEnvironmentVariable(name)
            : environment.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The directory installers are written to, honouring <c>--installer-dir</c>.</summary>
    public string ResolveInstallerRoot() =>
        InstallerDirectory is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(OutputDirectory, SourceLayout.InstallersDirectoryName);

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

    private static string HostArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };

    private static AiMode ParseAiMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "remote" => AiMode.Remote,
        "auto" => AiMode.Auto,
        _ => AiMode.Local,
    };
}
