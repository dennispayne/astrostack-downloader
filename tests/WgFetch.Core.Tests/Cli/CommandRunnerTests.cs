using System.Text.Json;
using WgFetch.Core.Cli;
using WgFetch.Core.Configuration;
using WgFetch.Core.Inference;
using WgFetch.Core.Model;
using WgFetch.Core.Output;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Progress;
using WgFetch.Core.Targets;
using WgFetch.Core.Tests.Support;
using YamlDotNet.RepresentationModel;

namespace WgFetch.Core.Tests.Cli;

/// <summary>
/// End-to-end coverage of the command surface with no network and no model files: every dependency
/// that touches the outside world is injected (docs/REQUIREMENTS.md, "Testing").
/// </summary>
public sealed class CommandRunnerTests
{
    private const string VendorUrl = "https://vendor.example.com/setup-3.1.0.exe";
    private const string PrereqsPresentUnverified = "present (unverified)";
    private const string PrereqsNotInstalled = "not installed";

    private static Recipe DemoRecipe() => new()
    {
        PackageId = "Demo.App",
        ComponentId = "demo",
        DisplayName = "Demo App",
        Publisher = "Demo Publisher",
        Aliases = ["demo app"],
        SourceKind = RecipeSourceKind.DirectUrl,
        DirectUrl = VendorUrl,
        VersionPattern = @"setup-(?<version>\d+\.\d+\.\d+)\.exe",
        Allowlist = ["vendor.example.com"],
        InstallerType = "exe",
    };

    /// <summary>The recipe as the user would hand it to <c>--recipe-inline</c>.</summary>
    private static string RecipeJson()
    {
        var recipe = DemoRecipe();
        return $$"""
            {
              "schemaVersion": {{recipe.SchemaVersion}},
              "packageId": "{{recipe.PackageId}}",
              "componentId": "{{recipe.ComponentId}}",
              "displayName": "{{recipe.DisplayName}}",
              "publisher": "{{recipe.Publisher}}",
              "aliases": ["demo app"],
              "sourceKind": "directUrl",
              "directUrl": "{{recipe.DirectUrl}}",
              "versionPattern": "setup-(?<version>\\d+\\.\\d+\\.\\d+)\\.exe",
              "allowlist": ["vendor.example.com"],
              "installerType": "exe"
            }
            """;
    }

    private static (CommandRunner Runner, StringWriter Out, StringWriter Error) CreateRunner(
        StubHttpGateway? http = null,
        IEmbeddingModel? embeddings = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        ITextGenerator? localGenerator = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Http = http ?? new StubHttpGateway(),
            Embeddings = embeddings,
            LocalGenerator = localGenerator,
            TimeProvider = TimeProvider.System,
            Environment = environment ?? new Dictionary<string, string?> { ["CI"] = "true" },
        });

        return (runner, stdout, stderr);
    }

    /// <summary>Options every verb accepts (docs/REQUIREMENTS.md, "CLI surface").</summary>
    private static string[] Repo(TempDirectory temp, params string[] args) =>
        [.. args, "--winget-repo", temp.Path];

    /// <summary>The richer option set the acquisition verbs accept, isolated from the user's real state.</summary>
    private static string[] Acq(TempDirectory temp, params string[] args) =>
    [
        .. args,
        "--winget-repo", temp.Path,
        "--cache-dir", temp.Combine("cache"),
        "--models-root", temp.Combine("models"),
    ];

    [Fact]
    public async Task Help_IsFastAndSucceeds()
    {
        var (runner, stdout, _) = CreateRunner();

        var exit = await runner.RunAsync(["--help"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("fetch", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_Interactive_ShowsSplashAndExistingHelp()
    {
        using var temp = new TempDirectory();
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false, WindowWidth = 80 },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.True(stdout.ToString().Count(character => character == '█') >= 40);
        Assert.Contains("resolve", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("download", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(stdout.ToString().Split("\u001b[38;2;", StringSplitOptions.None).Length >= 6);
        Assert.Contains("Usage: wgfetch <command> [options]", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_WithTargets_ShowsStatus()
    {
        using var temp = new TempDirectory();
        await TargetsFile.SaveAsync(
            new TargetsDocument
            {
                Targets = [new TargetEntry { Name = "nina", State = TargetState.Acquired }],
            },
            SourceLayout.TargetsPath(temp.Path),
            CancellationToken.None);
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("Targets acquired", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("·  1", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_WithStaleTargets_CountsThemAsAcquired()
    {
        using var temp = new TempDirectory();
        await TargetsFile.SaveAsync(
            new TargetsDocument
            {
                Targets =
                [
                    new TargetEntry { Name = "nina", State = TargetState.Acquired },
                    new TargetEntry { Name = "phd2", State = TargetState.Stale },
                ],
            },
            SourceLayout.TargetsPath(temp.Path),
            CancellationToken.None);
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("Targets acquired", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("·  2", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_RedactsSecretInSourcePath()
    {
        using var temp = new TempDirectory();
        const string secret = "topsecret-token-12345";
        var output = temp.Combine($"repo-{secret}");
        await TargetsFile.SaveAsync(
            new TargetsDocument
            {
                Targets = [new TargetEntry { Name = "nina", State = TargetState.Acquired }],
            },
            SourceLayout.TargetsPath(output),
            CancellationToken.None);
        var configPath = temp.Combine("config.json");
        await File.WriteAllTextAsync(
            configPath,
            $$"""{"outputDirectory":"{{output.Replace("\\", "\\\\", StringComparison.Ordinal)}}","aiKey":"{{secret}}"}""",
            CancellationToken.None);
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "dumb", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.DoesNotContain(secret, stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_ManifestPresentButAssetsMissing_ShowsNotInstalled()
    {
        // A bare install-manifest.json with none of the pinned model files on disk must never be
        // reported as present: landing readiness comes from the hashing-free asset presence probe, not
        // from the manifest's mere existence (a partial or wiped install is not ready).
        using var temp = new TempDirectory();
        await TargetsFile.SaveAsync(
            new TargetsDocument
            {
                Targets = [new TargetEntry { Name = "nina", State = TargetState.Acquired }],
            },
            SourceLayout.TargetsPath(temp.Path),
            CancellationToken.None);
        var modelsRoot = temp.Combine("models");
        Directory.CreateDirectory(modelsRoot);
        await File.WriteAllTextAsync(Path.Combine(modelsRoot, "install-manifest.json"), "{}", CancellationToken.None);
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?>
            {
                ["WGFETCH_OUTPUT"] = temp.Path,
                ["WGFETCH_MODELS"] = modelsRoot,
            },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains(PrereqsNotInstalled, stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_PresentEmbeddingAssets_AreReportedAsUnverified()
    {
        using var temp = new TempDirectory();
        await TargetsFile.SaveAsync(
            new TargetsDocument
            {
                Targets = [new TargetEntry { Name = "nina", State = TargetState.Acquired }],
            },
            SourceLayout.TargetsPath(temp.Path),
            CancellationToken.None);
        var modelsRoot = temp.Combine("models");
        WriteSparseEmbeddingAssets(modelsRoot);

        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?>
            {
                ["WGFETCH_OUTPUT"] = temp.Path,
                ["WGFETCH_MODELS"] = modelsRoot,
            },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains(PrereqsPresentUnverified, stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("installed", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoCommand_TruncatedEmbeddingAsset_IsNotReportedPresent()
    {
        using var temp = new TempDirectory();
        await TargetsFile.SaveAsync(
            new TargetsDocument
            {
                Targets = [new TargetEntry { Name = "nina", State = TargetState.Acquired }],
            },
            SourceLayout.TargetsPath(temp.Path),
            CancellationToken.None);
        var modelsRoot = temp.Combine("models");
        WriteSparseEmbeddingAssets(modelsRoot, firstAssetSizeOverride: FirstEmbeddingAssetSize() - 1);

        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?>
            {
                ["WGFETCH_OUTPUT"] = temp.Path,
                ["WGFETCH_MODELS"] = modelsRoot,
            },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains(PrereqsNotInstalled, stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PrereqsPresentUnverified, stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidSizedEmbeddingAssets))]
    public async Task NoCommand_InvalidSizedEmbeddingAsset_IsNotReportedPresent(string sizeCase, long firstAssetSize)
    {
        using var temp = new TempDirectory();
        await TargetsFile.SaveAsync(
            new TargetsDocument
            {
                Targets = [new TargetEntry { Name = "nina", State = TargetState.Acquired }],
            },
            SourceLayout.TargetsPath(temp.Path),
            CancellationToken.None);
        var modelsRoot = temp.Combine("models");
        WriteSparseEmbeddingAssets(modelsRoot, firstAssetSizeOverride: firstAssetSize);

        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?>
            {
                ["WGFETCH_OUTPUT"] = temp.Path,
                ["WGFETCH_MODELS"] = modelsRoot,
            },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);
        var rendered = stdout.ToString();

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.True(
            rendered.Contains(PrereqsNotInstalled, StringComparison.Ordinal),
            $"{sizeCase}: expected the landing view to report invalid-sized assets as not installed.");
        Assert.False(
            rendered.Contains(PrereqsPresentUnverified, StringComparison.Ordinal),
            $"{sizeCase}: expected the landing view not to report invalid-sized assets as present.");
    }

    public static TheoryData<string, long> InvalidSizedEmbeddingAssets()
    {
        return new TheoryData<string, long>
        {
            { "zero-byte", 0 },
            { "oversized", FirstEmbeddingAssetSize() + 1 },
            { "substantially-oversized", FirstEmbeddingAssetSize() + (1024 * 1024) },
        };
    }

    [Fact]
    public async Task NoCommand_Json_WritesUsageErrorToStandardError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--json"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("no command given", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("resolve  •", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_Plain_ShowsColorlessSplash()
    {
        using var temp = new TempDirectory();
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("Get started", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("╭", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_InvalidSettings_StillEmitsHeaderBeforeError()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await File.WriteAllTextAsync(configPath, """{"outputDirectory":"bad\u0000path"}""", CancellationToken.None);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.StartsWith("wgfetch", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("invalid configuration", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_Redirected_ShowsExistingUsageErrorOnly()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { OutputRedirected = true, Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("no command given", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageError_SanitizesParserSuppliedText()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr);

        var exit = await runner.RunAsync(["bad\u001b[31m"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Empty(stdout.ToString());
        Assert.DoesNotContain("\u001b", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("bad?[31m", stderr.ToString(), StringComparison.Ordinal);
    }

    private static void WriteSparseEmbeddingAssets(string modelsRoot, long? firstAssetSizeOverride = null)
    {
        // The no-command prerequisite probe is intentionally metadata-only for startup latency; these
        // sparse files exercise its size checks without hashing or writing model-sized byte content.
        for (var i = 0; i < PinnedModels.Embedding.Assets.Count; i++)
        {
            var asset = PinnedModels.Embedding.Assets[i];
            var path = Path.Combine(PrereqInstaller.ModelDirectory(modelsRoot, PinnedModels.Embedding), asset.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (asset.SizeBytes is not { } size)
            {
                throw new InvalidOperationException($"The sparse fixture requires a pinned size for {asset.RelativePath}.");
            }

            if (firstAssetSizeOverride.HasValue && i == 0)
            {
                size = firstAssetSizeOverride.Value;
            }

            using var file = File.Create(path);
            file.SetLength(size);
        }
    }

    private static long FirstEmbeddingAssetSize()
    {
        if (PinnedModels.Embedding.Assets[0].SizeBytes is { } size)
        {
            if (size <= 0)
            {
                throw new InvalidOperationException("The first embedding asset must be non-empty for invalid-size regressions.");
            }

            return size;
        }

        throw new InvalidOperationException("The first embedding asset must have a pinned size for invalid-size regressions.");
    }

    [Fact]
    public async Task NoCommand_WithPositionalAfterOptionTerminator_ShowsExistingUsageErrorOnly()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--", "stray"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("no command given", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_MalformedTargets_ShowsLandingAndUsageError()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "targets.yaml"), "targets: [\n", CancellationToken.None);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("unable to read targets.yaml", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("unable to read targets.yaml", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_SchemaInvalidTargets_ShowsLandingAndUsageError()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "targets.yaml"),
            """
            ? [invalid]
            : value
            targets: []
            """,
            CancellationToken.None);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("unable to read targets.yaml", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("unable to read targets.yaml", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_TargetsPathIsDirectory_FailsClosedInsteadOfFirstRun()
    {
        using var temp = new TempDirectory();

        // A directory named targets.yaml is not a missing file: File.Exists(path) reports false for
        // it (same as a genuinely absent file), so the landing view must not rely on that check alone
        // to decide "first run" — it must attempt to read the path and fail closed on what it finds.
        Directory.CreateDirectory(Path.Combine(temp.Path, "targets.yaml"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("unable to read targets.yaml", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("unable to read targets.yaml", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_OutputPathIsFile_FailsClosedInsteadOfFirstRun()
    {
        using var temp = new TempDirectory();
        var output = temp.Combine("source");
        await File.WriteAllTextAsync(output, "not a directory", CancellationToken.None);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = output },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("unable to read targets.yaml", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("unable to read targets.yaml", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status", null, "targets: [\n")]
    [InlineData("remove", "nina", "targets: [\n")]
    [InlineData("status", null, "version: 1\nversion: 2\n")]
    [InlineData("status", null, "targets:\n  - name: nina\n    name: phd2\n")]
    public async Task Command_MalformedTargets_FailsClosedWithUsageError(string command, string? argument, string yaml)
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "targets.yaml"), yaml, CancellationToken.None);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
        });

        string[] args = argument is null ? [command] : [command, argument];
        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("failed to parse targets file", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("targets.yaml", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("re-add your targets", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_MalformedFromFile_UsesInputFileGuidance()
    {
        using var temp = new TempDirectory();
        var inputPath = temp.Combine("apps.yaml");
        await File.WriteAllTextAsync(inputPath, "targets: [\n", CancellationToken.None);
        var stderr = new StringWriter();
        var runner = new CommandRunner(new StringWriter(), stderr, new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Combine("source") },
        });

        var exit = await runner.RunAsync(["add", "--from-file", inputPath], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("input file", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("re-add your targets", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_SchemaInvalidTargets_FailsClosedWithUsageError()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "targets.yaml"),
            """
            ? [invalid]
            : value
            targets: []
            """,
            CancellationToken.None);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
        });

        var exit = await runner.RunAsync(["status"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("failed to parse targets file", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_TargetsError_RedactsConfiguredSecrets()
    {
        using var temp = new TempDirectory();
        var secret = "topsecret-token-12345";
        var output = temp.Combine($"repo-{secret}");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "targets.yaml"), "targets: [\n", CancellationToken.None);
        var configPath = temp.Combine("config.json");
        await File.WriteAllTextAsync(
            configPath,
            $$"""{"outputDirectory":"{{output.Replace("\\", "\\\\", StringComparison.Ordinal)}}","aiKey":"{{secret}}"}""",
            CancellationToken.None);

        var stderr = new StringWriter();
        var runner = new CommandRunner(new StringWriter(), stderr, new RunnerDependencies());

        var exit = await runner.RunAsync(["status", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.DoesNotContain(secret, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_Cancelled_ReturnsCancelledExitCode()
    {
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync([], new CancellationToken(canceled: true));

        Assert.Equal(ExitCode.Cancelled, exit);
        Assert.Contains("cancelled", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_UnreadableConfig_PreservesLandingAndUsageError()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await File.WriteAllTextAsync(configPath, "{}", CancellationToken.None);
        await using var lockedConfig = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            Environment = new Dictionary<string, string?> { ["WGFETCH_OUTPUT"] = temp.Path },
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false, WindowWidth = 80 },
        });

        var exit = await runner.RunAsync(["--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        // Each tagline word is wrapped in its own gradient-colored markup span, so ANSI reset/color
        // codes are interspersed between words — assert on the words themselves rather than the
        // exact contiguous phrase (see SplashScreenTests for the same technique).
        var output = stdout.ToString();
        Assert.Contains("resolve", output, StringComparison.Ordinal);
        Assert.Contains("verify", output, StringComparison.Ordinal);
        Assert.Contains("download", output, StringComparison.Ordinal);
        Assert.Contains("Usage: wgfetch <command> [options]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_InvalidConfigPathValue_ReturnsUsageErrorWithoutCrash()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await File.WriteAllTextAsync(
            configPath,
            """{ "outputDirectory": "bad\u0000path" }""",
            CancellationToken.None);
        var stderr = new StringWriter();
        var runner = new CommandRunner(new StringWriter(), stderr, new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("invalid configuration or option value", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_IsAUsageError()
    {
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(["frobnicate"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("wgfetch", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--arch", "bogus", "x64, x86, arm64")]
    [InlineData("--scope", "bogus", "machine, user")]
    [InlineData("--log-level", "bogus", "trace, debug, info, warn, error, none")]
    [InlineData("--ai-mode", "bogus", "local, remote, auto")]
    public async Task Invalid_closed_enum_value_IsAUsageError(string option, string value, string allowed)
    {
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(["resolve", "demo", option, value, "--dry-run"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains(
            $"wgfetch: invalid value '{value}' for {option} (expected one of: {allowed})",
            stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddThenStatus_WritesTargetsAndReportsListed()
    {
        using var temp = new TempDirectory();
        var (runner, stdout, _) = CreateRunner();

        var add = await runner.RunAsync(Acq(temp, "add", "nina", "phd2"), CancellationToken.None);
        Assert.Equal(ExitCode.Success, add);

        var document = await TargetsFile.LoadAsync(SourceLayout.TargetsPath(temp.Path), CancellationToken.None);
        Assert.Equal(2, document.Targets.Count);

        var (statusRunner, statusOut, _) = CreateRunner();
        var status = await statusRunner.RunAsync(Repo(temp, "status"), CancellationToken.None);

        Assert.Equal(ExitCode.Success, status);
        Assert.Contains("nina", statusOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("listed", statusOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("nina", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_OversizedTargetsFile_ReturnsUsageErrorInsteadOfCrashing()
    {
        using var temp = new TempDirectory();
        var path = SourceLayout.TargetsPath(temp.Path);

        // Bypasses TargetsFile.SaveAsync's own size guard so the setup file, just under the limit,
        // still loads cleanly; adding a new entry then pushes the re-rendered file over MaxFileBytes.
        var seed = new TargetsDocument
        {
            ExtraFields = new Dictionary<string, YamlNode>(StringComparer.Ordinal)
            {
                ["huge"] = new YamlScalarNode(new string('x', (8 * 1024 * 1024) - 16)),
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, TargetsFile.Render(seed), CancellationToken.None);

        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(Acq(temp, "add", "nina"), CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("failed to parse targets file", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_IsIdempotent()
    {
        using var temp = new TempDirectory();

        for (var i = 0; i < 2; i++)
        {
            var (runner, _, _) = CreateRunner();
            Assert.Equal(ExitCode.Success, await runner.RunAsync(Acq(temp, "add", "nina"), CancellationToken.None));
        }

        var document = await TargetsFile.LoadAsync(SourceLayout.TargetsPath(temp.Path), CancellationToken.None);
        Assert.Single(document.Targets);
    }

    [Fact]
    public async Task Remove_DropsTheEntry()
    {
        using var temp = new TempDirectory();
        var (adder, _, _) = CreateRunner();
        await adder.RunAsync(Acq(temp, "add", "nina", "phd2"), CancellationToken.None);

        var (runner, _, _) = CreateRunner();
        var exit = await runner.RunAsync(Repo(temp, "remove", "nina"), CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        var document = await TargetsFile.LoadAsync(SourceLayout.TargetsPath(temp.Path), CancellationToken.None);
        Assert.Equal("phd2", Assert.Single(document.Targets).Name);
    }

    [Fact]
    public async Task Fetch_AcceptsAVerifiedInstallerAndEmitsTheWholeSource()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(payload));

        var (runner, stdout, stderr) = CreateRunner(http);
        var exit = await runner.RunAsync(
            Acq(temp, "fetch", "demo", "--recipe-inline", RecipeJson()),
            CancellationToken.None);

        Assert.True(exit == ExitCode.Success, $"exit={exit} stderr={stderr}");

        var installer = Path.Combine(temp.Path, "installers", "Demo.App", "3.1.0", "setup-3.1.0.exe");
        Assert.True(File.Exists(installer), $"expected {installer}; stdout={stdout}");
        Assert.Equal(payload, await File.ReadAllBytesAsync(installer, CancellationToken.None));

        // The full local-source layout, not just the bytes.
        Assert.True(File.Exists(SourceLayout.IndexDatabasePath(temp.Path)));
        Assert.True(File.Exists(SourceLayout.RestInformationPath(temp.Path)));
        Assert.True(File.Exists(SourceLayout.RestPackageManifestPath(temp.Path, "Demo.App")));
        Assert.True(File.Exists(Path.Combine(
            SourceLayout.ManifestDirectory(temp.Path, "Demo.App", "3.1.0"),
            SourceLayout.VersionManifestFileName("Demo.App"))));

        var provenance = await new ProvenanceWriter().ReadAllAsync(temp.Path, CancellationToken.None);
        var record = Assert.Single(provenance);
        Assert.Equal(VendorUrl, record.AcceptedUrl);
        Assert.False(string.IsNullOrEmpty(record.Sha256));
    }

    [Fact]
    public async Task Fetch_RejectsAnHtmlLoginPageAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Html("<html>Please sign in</html>"));

        var (runner, _, stderr) = CreateRunner(http);
        var exit = await runner.RunAsync(
            Acq(temp, "fetch", "demo", "--recipe-inline", RecipeJson()),
            CancellationToken.None);

        Assert.NotEqual(ExitCode.Success, exit);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "installers")));
        Assert.False(File.Exists(SourceLayout.IndexDatabasePath(temp.Path)));
        Assert.Contains("demo", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRunFetch_DownloadsNothing()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));

        var (runner, stdout, _) = CreateRunner(http);
        var exit = await runner.RunAsync(
            Acq(temp, "fetch", "demo", "--recipe-inline", RecipeJson(), "--dry-run"),
            CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("dry-run", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "installers")));
    }

    [Fact]
    public async Task Resolve_ReportsTheUrlWithoutDownloading()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));

        var (runner, stdout, _) = CreateRunner(http);
        var exit = await runner.RunAsync(
            Acq(temp, "resolve", "demo", "--recipe-inline", RecipeJson()),
            CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains(VendorUrl, stdout.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "installers")));
    }

    [Fact]
    public async Task UnknownName_WithoutModels_IsAMissingPrerequisiteWithTheExactCommand()
    {
        using var temp = new TempDirectory();
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(
            Acq(temp, "fetch", "some application nobody has heard of"),
            CancellationToken.None);

        Assert.Equal(ExitCode.MissingPrerequisite, exit);
        Assert.Contains("wgfetch prereqs install", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FuzzyName_WithEmbeddings_ResolvesAndFetches()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));
        var (runner, stdout, _) = CreateRunner(http, new FakeEmbeddingModel());

        var exit = await runner.RunAsync(
            Acq(
                temp,
                "fetch",
                "Demo Publisher",
                "--recipe-inline",
                RecipeJson(),
                "--threshold",
                "0.1",
                "--dry-run"),
            CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Demo.App", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("dry-run", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FuzzyName_BelowThreshold_ReportsRankedCandidatesAsAmbiguous()
    {
        using var temp = new TempDirectory();
        var (runner, _, stderr) = CreateRunner(embeddings: new FakeEmbeddingModel());

        var exit = await runner.RunAsync(
            Acq(
                temp,
                "fetch",
                "Demo Publisher",
                "--recipe-inline",
                RecipeJson(),
                "--threshold",
                "1"),
            CancellationToken.None);

        Assert.Equal(ExitCode.Ambiguous, exit);
        Assert.Contains("ambiguous", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("demo", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FuzzyName_BelowThreshold_UsesTierTwoSelection()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));
        var generator = new ScriptedTextGenerator().Enqueue("CHOSEN_ID: demo");
        var (runner, stdout, _) = CreateRunner(
            http,
            new FakeEmbeddingModel(),
            localGenerator: generator);

        var exit = await runner.RunAsync(
            Acq(
                temp,
                "fetch",
                "Demo Publisher",
                "--recipe-inline",
                RecipeJson(),
                "--threshold",
                "1",
                "--dry-run"),
            CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Equal(1, generator.CallCount);
        Assert.Contains("Demo.App", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonMode_EmitsOneJsonObjectPerLineOnStdout()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));

        var (runner, stdout, _) = CreateRunner(http);
        await runner.RunAsync(
            Acq(temp, "fetch", "demo", "--recipe-inline", RecipeJson(), "--json"),
            CancellationToken.None);

        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("event", out _), line);
        }
    }

    [Theory]
    [InlineData(false, "targets: [this is: not valid: yaml:::", null)]
    [InlineData(true, "targets: [this is: not valid: yaml:::", null)]
    [InlineData(false, "version: nope", "invalid-version")]
    [InlineData(true, "version: nope", "invalid-version")]
    [InlineData(false, "version: 999999999999999999999", "invalid-version")]
    [InlineData(true, "version: 999999999999999999999", "invalid-version")]
    [InlineData(false, "version: 1\nversion: 2\ntargets: []", TargetsFileException.InvalidDocumentReasonCode)]
    [InlineData(true, "version: 1\nversion: 2\ntargets: []", TargetsFileException.InvalidDocumentReasonCode)]
    public async Task MalformedTargetsYaml_IsCleanUsageError(bool json, string yaml, string? reasonCode)
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(
            SourceLayout.TargetsPath(temp.Path),
            yaml);

        var (runner, stdout, stderr) = CreateRunner();
        var arguments = Repo(temp, "status");
        if (json)
        {
            arguments = [.. arguments, "--json"];
        }

        var exit = await runner.RunAsync(arguments, CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("failed to parse targets file", json ? stdout.ToString() : stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stderr.ToString(), StringComparison.Ordinal);

        if (json)
        {
            Assert.Empty(stderr.ToString());
            using var document = JsonDocument.Parse(Assert.Single(
                stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            Assert.Equal("error", document.RootElement.GetProperty("event").GetString());
            Assert.Equal("targets", document.RootElement.GetProperty("stage").GetString());
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("exitCode").GetInt32());

            string message = document.RootElement.GetProperty("message").GetString()!;
            Assert.Contains("failed to parse targets file", message, StringComparison.Ordinal);
            if (reasonCode is not null)
            {
                Assert.Contains($"reason: {reasonCode}", message, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Single(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    [Fact]
    public async Task Add_FromFileWithMalformedTargetsYaml_ReportsSourcePath()
    {
        using var temp = new TempDirectory();
        string inputPath = temp.Combine("invalid-targets.yaml");
        await File.WriteAllTextAsync(inputPath, "version: nope");

        var (runner, _, stderr) = CreateRunner();
        var exit = await runner.RunAsync(
            Acq(temp, "add", "--from-file", inputPath),
            CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains($"'{inputPath}'", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAndList_ReadBackWhatFetchWrote()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));

        var (fetcher, _, _) = CreateRunner(http);
        await fetcher.RunAsync(Acq(temp, "fetch", "demo", "--recipe-inline", RecipeJson()), CancellationToken.None);

        var (lister, listOut, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await lister.RunAsync(Repo(temp, "list"), CancellationToken.None));
        Assert.Contains("Demo.App", listOut.ToString(), StringComparison.Ordinal);

        var (verifier, _, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await verifier.RunAsync(Repo(temp, "verify"), CancellationToken.None));
    }

    [Fact]
    public async Task Verify_DetectsATamperedInstaller()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));

        var (fetcher, _, _) = CreateRunner(http);
        await fetcher.RunAsync(Acq(temp, "fetch", "demo", "--recipe-inline", RecipeJson()), CancellationToken.None);

        var installer = Path.Combine(temp.Path, "installers", "Demo.App", "3.1.0", "setup-3.1.0.exe");
        var bytes = await File.ReadAllBytesAsync(installer, CancellationToken.None);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(installer, bytes, CancellationToken.None);

        var (verifier, _, stderr) = CreateRunner();
        var exit = await verifier.RunAsync(Repo(temp, "verify"), CancellationToken.None);

        Assert.Equal(ExitCode.HashMismatch, exit);
        Assert.Contains("does not match", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_ProducesAstroStackDscPatches()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(VendorUrl, StubResponse.Binary(FakeInstaller.PortableExecutable()));

        var (fetcher, _, _) = CreateRunner(http);
        await fetcher.RunAsync(Acq(temp, "fetch", "demo", "--recipe-inline", RecipeJson()), CancellationToken.None);

        var (adder, _, _) = CreateRunner();
        await adder.RunAsync(Acq(temp, "add", "demo"), CancellationToken.None);

        var outDirectory = temp.Combine("export");
        var (runner, _, _) = CreateRunner();
        var exit = await runner.RunAsync(Repo(temp, "export", "--out", outDirectory), CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.True(Directory.Exists(Path.Combine(outDirectory, "patches")));
    }

    [Fact]
    public async Task PrereqsStatus_ReportsMissingModels()
    {
        using var temp = new TempDirectory();
        var (runner, stdout, _) = CreateRunner();

        var exit = await runner.RunAsync(["prereqs", "status", "--models-dir", temp.Combine("models")], CancellationToken.None);

        Assert.Equal(ExitCode.MissingPrerequisite, exit);
        Assert.Contains("e5-small-v2", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_SetGetListAndUnset_Persist()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        var (setter, setOut, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await setter.RunAsync(
            ["config", "set", "output-directory", temp.Combine("source"), "--config", configPath],
            CancellationToken.None));
        Assert.Contains("saved", setOut.ToString(), StringComparison.Ordinal);

        var (getter, getOut, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await getter.RunAsync(
            ["config", "get", "outputDirectory", "--config", configPath],
            CancellationToken.None));
        Assert.Contains(temp.Combine("source"), getOut.ToString(), StringComparison.Ordinal);

        var (lister, listOut, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await lister.RunAsync(["config", "list", "--config", configPath], CancellationToken.None));
        Assert.Contains("outputDirectory", listOut.ToString(), StringComparison.Ordinal);

        var (unsetter, _, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await unsetter.RunAsync(
            ["config", "unset", "outputDirectory", "--config", configPath],
            CancellationToken.None));
        Assert.Null((await ConfigFile.LoadAsync(configPath, CancellationToken.None)).OutputDirectory);
    }

    [Fact]
    public async Task Config_commands_repair_invalid_persisted_paths_without_resolving_run_settings()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await File.WriteAllTextAsync(configPath, """{"outputDirectory":"\u0000"}""", CancellationToken.None);

        var (runner, _, _) = CreateRunner();
        var exit = await runner.RunAsync(["config", "unset", "outputDirectory", "--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Null((await ConfigFile.LoadAsync(configPath, CancellationToken.None)).OutputDirectory);
    }

    [Fact]
    public async Task Config_json_emits_redacted_machine_events()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        await ConfigFile.SaveAsync(new WgFetchConfig { OutputDirectory = temp.Combine("source") }, configPath, CancellationToken.None);
        var (runner, stdout, stderr) = CreateRunner();

        var exit = await runner.RunAsync(["config", "get", "outputDirectory", "--config", configPath, "--json"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var document = JsonDocument.Parse(Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("config", document.RootElement.GetProperty("event").GetString());
        Assert.Equal("outputDirectory", document.RootElement.GetProperty("target").GetString());
        Assert.Contains(temp.Combine("source"), document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(temp.Combine("source"), stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_get_and_list_redact_effective_environment_secrets_in_persisted_values()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        const string envSecret = "environment-only-secret";
        await ConfigFile.SaveAsync(
            new WgFetchConfig { AiEndpoint = $"https://ai.example/v1/{envSecret}?custom=value" },
            configPath,
            CancellationToken.None);
        var environment = new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["WGFETCH_AI_KEY"] = envSecret,
        };

        var (getter, getOut, _) = CreateRunner(environment: environment);
        Assert.Equal(ExitCode.Success, await getter.RunAsync(
            ["config", "get", "aiEndpoint", "--config", configPath],
            CancellationToken.None));

        var (lister, listOut, _) = CreateRunner(environment: environment);
        Assert.Equal(ExitCode.Success, await lister.RunAsync(["config", "list", "--config", configPath], CancellationToken.None));

        Assert.DoesNotContain(envSecret, getOut.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(envSecret, listOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", getOut.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REDACTED", listOut.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Config_json_redacts_effective_environment_secrets_in_machine_events()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        const string envSecret = "environment-only-secret";
        await ConfigFile.SaveAsync(
            new WgFetchConfig { SearchEndpoint = $"https://search.example/{envSecret}" },
            configPath,
            CancellationToken.None);
        var environment = new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["WGFETCH_SEARCH_KEY"] = envSecret,
        };
        var (runner, stdout, stderr) = CreateRunner(environment: environment);

        var exit = await runner.RunAsync(
            ["config", "get", "searchEndpoint", "--config", configPath, "--json"],
            CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.DoesNotContain(envSecret, stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(envSecret, stderr.ToString(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Contains("REDACTED", document.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("unset")]
    public async Task Config_unknown_setting_errors_redact_effective_environment_secrets(string subcommand)
    {
        using var temp = new TempDirectory();
        const string envSecret = "environment-only-secret";
        var environment = new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["WGFETCH_AI_KEY"] = envSecret,
        };
        var args = subcommand switch
        {
            "set" => new[] { "config", subcommand, envSecret, "value", "--config", temp.Combine("config.json") },
            _ => ["config", subcommand, envSecret, "--config", temp.Combine("config.json")],
        };
        var (runner, _, stderr) = CreateRunner(environment: environment);

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.DoesNotContain(envSecret, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Config_unknown_subcommand_error_redacts_effective_environment_secrets()
    {
        using var temp = new TempDirectory();
        const string envSecret = "environment-only-secret";
        var environment = new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["WGFETCH_AI_KEY"] = envSecret,
        };
        var (runner, _, stderr) = CreateRunner(environment: environment);

        var exit = await runner.RunAsync(
            ["config", envSecret, "--config", temp.Combine("config.json")],
            CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.DoesNotContain(envSecret, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Config_set_does_not_overwrite_a_malformed_existing_config()
    {
        using var temp = new TempDirectory();
        const string pathSecret = "path-token-secret";
        var directory = temp.Combine(pathSecret);
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        const string malformed = "{ this is not json";
        await File.WriteAllTextAsync(configPath, malformed, CancellationToken.None);
        var environment = new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["WGFETCH_AI_KEY"] = pathSecret,
        };
        var (runner, _, stderr) = CreateRunner(environment: environment);

        var exit = await runner.RunAsync(
            ["config", "set", "plain", "true", "--config", configPath],
            CancellationToken.None);

        Assert.Equal(ExitCode.ConfigurationError, exit);
        Assert.Contains("malformed", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pathSecret, stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(malformed, await File.ReadAllTextAsync(configPath, CancellationToken.None));
    }

    [Fact]
    public async Task Config_set_reports_a_configuration_error_when_the_config_path_is_unwritable()
    {
        using var temp = new TempDirectory();
        const string pathSecret = "path-token-secret";
        var directory = temp.Combine(pathSecret);
        Directory.CreateDirectory(directory);
        // A directory can never be opened as the config file: this exercises the I/O failure path
        // (as opposed to the malformed-JSON path) without requiring platform-specific permission bits.
        var configPath = directory;
        var environment = new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["WGFETCH_AI_KEY"] = pathSecret,
        };
        var (runner, _, stderr) = CreateRunner(environment: environment);

        var exit = await runner.RunAsync(
            ["config", "set", "plain", "true", "--config", configPath],
            CancellationToken.None);

        Assert.Equal(ExitCode.ConfigurationError, exit);
        Assert.DoesNotContain(pathSecret, stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aiKey")]
    [InlineData("search-key")]
    [InlineData("github-token")]
    public async Task Config_set_redacts_the_pending_credential_when_the_update_fails(string name)
    {
        using var temp = new TempDirectory();
        const string pendingSecret = "pending-secret-value";
        var secretDirectory = temp.Combine(pendingSecret);
        Directory.CreateDirectory(secretDirectory);
        var configPath = Path.Combine(secretDirectory, "config.json");
        await File.WriteAllTextAsync(configPath, "{ this is not json", CancellationToken.None);
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(
            ["config", "set", name, pendingSecret, "--config", configPath],
            CancellationToken.None);

        Assert.Equal(ExitCode.ConfigurationError, exit);
        Assert.DoesNotContain(pendingSecret, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Config_set_redacts_the_pending_credential_when_initial_config_load_fails()
    {
        using var temp = new TempDirectory();
        const string pendingSecret = "pending-secret-value";
        var configPath = temp.Combine(pendingSecret);
        Directory.CreateDirectory(configPath);
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(
            ["config", "set", "aiKey", pendingSecret, "--config", configPath],
            CancellationToken.None);

        Assert.Equal(ExitCode.ConfigurationError, exit);
        Assert.DoesNotContain(pendingSecret, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Config_set_reports_a_configuration_error_instead_of_crashing_for_an_invalid_config_path()
    {
        // Path.GetFullPath (called from ConfigFile's AcquireLockAsync) throws ArgumentException for a
        // path containing a NUL character; this must be translated the same way as other config-file
        // I/O failures rather than escaping RunAsync.
        var configPath = "config\0invalid.json";
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(
            ["config", "set", "plain", "true", "--config", configPath],
            CancellationToken.None);

        Assert.Equal(ExitCode.ConfigurationError, exit);
        Assert.NotEmpty(stderr.ToString());
    }

    [Fact]
    public async Task Any_command_reports_a_configuration_error_instead_of_crashing_when_the_config_file_cannot_be_read()
    {
        using var temp = new TempDirectory();
        const string pathSecret = "path-token-secret";
        var directory = temp.Combine(pathSecret);
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        await ConfigFile.SaveAsync(new WgFetchConfig { Scope = "user" }, configPath, CancellationToken.None);
        var environment = new Dictionary<string, string?>
        {
            ["CI"] = "true",
            ["WGFETCH_AI_KEY"] = pathSecret,
        };
        var (runner, _, stderr) = CreateRunner(environment: environment);

        // Hold the file exclusively so ConfigFile.LoadAsync's own File.OpenRead fails: previously this
        // exception escaped ExecuteAsync unhandled for every command, including plain "status".
        using (new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var exit = await runner.RunAsync(["status", "--config", configPath], CancellationToken.None);

            Assert.Equal(ExitCode.ConfigurationError, exit);
            Assert.DoesNotContain(pathSecret, stderr.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Config_interactive_rejects_plain_terminal_mode()
    {
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(["config", "--interactive"], CancellationToken.None);

        Assert.Equal(ExitCode.Ambiguous, exit);
        Assert.Contains("requires an attached terminal", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_rejects_interactive_combined_with_a_subcommand()
    {
        using var temp = new TempDirectory();
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(
            ["config", "list", "--interactive", "--config", temp.Combine("config.json")],
            CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("cannot be combined with a subcommand", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aiKey", "")]
    [InlineData("searchProvider", "typo")]
    [InlineData("modelsRoot", "")]
    public async Task Config_rejects_invalid_or_nonpersistable_values(string name, string value)
    {
        using var temp = new TempDirectory();
        var (runner, _, stderr) = CreateRunner();

        var exit = await runner.RunAsync(
            ["config", "set", name, value, "--config", temp.Combine("config.json")],
            CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.False(File.Exists(temp.Combine("config.json")));
        Assert.NotEmpty(stderr.ToString());
    }

    [Theory]
    [InlineData("aiKey")]
    [InlineData("searchKey")]
    [InlineData("githubToken")]
    public async Task Config_persists_credentials_and_redacts_them_on_read(string name)
    {
        using var temp = new TempDirectory();
        var configPath = temp.Combine("config.json");
        var (setter, _, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await setter.RunAsync(
            ["config", "set", name, "super-secret-value", "--config", configPath],
            CancellationToken.None));

        var (getter, getOut, _) = CreateRunner();
        Assert.Equal(ExitCode.Success, await getter.RunAsync(
            ["config", "get", name, "--config", configPath],
            CancellationToken.None));
        Assert.DoesNotContain("super-secret-value", getOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", getOut.ToString(), StringComparison.OrdinalIgnoreCase);

        var raw = await File.ReadAllTextAsync(configPath, CancellationToken.None);
        Assert.Contains("super-secret-value", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecipesValidate_AcceptsEveryBundledSeedRecipe()
    {
        using var temp = new TempDirectory();
        var (runner, stdout, _) = CreateRunner();

        var exit = await runner.RunAsync(["recipes", "validate", "--cache-dir", temp.Combine("cache")], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("valid", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_WritesARedactedBundleWithNoSecrets()
    {
        using var temp = new TempDirectory();
        var token = "gh" + "p_" + new string('B', 36);
        var outDirectory = temp.Combine("diag");

        // A secret that leaked into an artifact the bundle copies must not survive the copy.
        await File.WriteAllTextAsync(
            SourceLayout.ProvenancePath(temp.Path),
            $"[{{\"acceptedUrl\":\"https://vendor.example.com/setup.exe?token={token}\"}}]",
            CancellationToken.None);

        var (runner, _, _) = CreateRunner();
        var exit = await runner.RunAsync(
            ["diagnostics", "--out", outDirectory, "--output", temp.Path],
            CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);

        foreach (var file in Directory.EnumerateFiles(outDirectory))
        {
            var text = await File.ReadAllTextAsync(file, CancellationToken.None);
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancellation_ExitsOneThirty()
    {
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (runner, _, _) = CreateRunner();
        var exit = await runner.RunAsync(Acq(temp, "add", "nina"), cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
    }
}
