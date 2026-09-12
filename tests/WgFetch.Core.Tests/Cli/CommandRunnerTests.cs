using System.Text.Json;
using WgFetch.Core.Cli;
using WgFetch.Core.Inference;
using WgFetch.Core.Model;
using WgFetch.Core.Output;
using WgFetch.Core.Prereqs;
using WgFetch.Core.Progress;
using WgFetch.Core.Targets;
using WgFetch.Core.Tests.Support;

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
        IEmbeddingModel? embeddings = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Http = http ?? new StubHttpGateway(),
            Embeddings = embeddings,
            TimeProvider = TimeProvider.System,
            Environment = new Dictionary<string, string?> { ["CI"] = "true" },
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
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("resolve  •  verify  •  download", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;99;102;241m", stdout.ToString(), StringComparison.Ordinal);
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
                // Keep future invalid-size scenarios from failing later with FileStream.SetLength's
                // less-specific range exception if a negative override is accidentally supplied.
                if (firstAssetSizeOverride.Value < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(firstAssetSizeOverride),
                        firstAssetSizeOverride.Value,
                        "Sparse asset size overrides must be non-negative.");
                }

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
        Assert.Contains("unreadable or malformed", stderr.ToString(), StringComparison.Ordinal);
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
        Assert.Contains("unreadable or malformed", stderr.ToString(), StringComparison.Ordinal);
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
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--config", configPath], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("resolve  •  verify  •  download", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: wgfetch <command> [options]", stdout.ToString(), StringComparison.Ordinal);
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
