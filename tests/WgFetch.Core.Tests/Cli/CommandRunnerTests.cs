using System.Text.Json;
using WgFetch.Core.Cli;
using WgFetch.Core.Configuration;
using WgFetch.Core.Inference;
using WgFetch.Core.Model;
using WgFetch.Core.Output;
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
        IEmbeddingModel? embeddings = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var runner = new CommandRunner(stdout, stderr, new RunnerDependencies
        {
            Http = http ?? new StubHttpGateway(),
            Embeddings = embeddings,
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
