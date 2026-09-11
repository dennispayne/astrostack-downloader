using System.Text.Json;
using WgFetch.Core.Cli;
using WgFetch.Core.Inference;
using WgFetch.Core.Model;
using WgFetch.Core.Output;
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
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync([], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("resolve  •  verify  •  download", stdout.ToString(), StringComparison.Ordinal);
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

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Targets acquired", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("·  1", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_Json_EmitsOnlyUsageErrorEvent()
    {
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--json"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("error", json.RootElement.GetProperty("event").GetString());
        Assert.DoesNotContain("resolve  •", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommand_Plain_ShowsColorlessSplash()
    {
        var stdout = new StringWriter();
        var runner = new CommandRunner(stdout, new StringWriter(), new RunnerDependencies
        {
            TerminalEnvironment = new TerminalEnvironment { Term = "xterm-256color", IsWindows = false },
        });

        var exit = await runner.RunAsync(["--plain"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Get started", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("╭", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", stdout.ToString(), StringComparison.Ordinal);
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
