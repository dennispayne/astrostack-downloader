using WgFetch.Core.Configuration;
using WgFetch.Core.Logging;
using WgFetch.Core.Tests.Support;

namespace WgFetch.Core.Tests.Configuration;

/// <summary>
/// Configuration is optional and a broken file is never fatal; secrets in it must never survive into a
/// diagnostics bundle (docs/REQUIREMENTS.md, "Configuration" and "Privacy").
/// </summary>
public sealed class ConfigFileTests
{
    [Fact]
    public async Task A_missing_file_yields_defaults()
    {
        using var temp = new TempDirectory();

        var config = await ConfigFile.LoadAsync(temp.Combine("absent.json"), CancellationToken.None);

        Assert.Null(config.OutputDirectory);
        Assert.Null(config.AiKey);
    }

    [Fact]
    public async Task A_malformed_file_is_not_fatal()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not json", CancellationToken.None);

        var config = await ConfigFile.LoadAsync(path, CancellationToken.None);

        Assert.Null(config.OutputDirectory);
    }

    [Fact]
    public async Task Round_trips_every_field()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("nested", "config.json");
        var original = new WgFetchConfig
        {
            OutputDirectory = "/srv/source",
            CacheDirectory = "/srv/cache",
            ModelsRoot = "/srv/models",
            Threshold = 0.66,
            Architecture = "x64",
            Scope = "machine",
            KeepVersions = 3,
            AiMode = "local",
            AiEndpoint = "https://localhost:11434/v1",
            AiModel = "phi-3.5-mini",
            SearchProvider = "mojeek",
            SearchEndpoint = "https://searx.example",
            LogLevel = "debug",
            ParallelDownloads = 4,
            MaxPerHost = 2,
            Plain = true,
        };

        await ConfigFile.SaveAsync(original, path, CancellationToken.None);
        var loaded = await ConfigFile.LoadAsync(path, CancellationToken.None);

        Assert.Equal(original, loaded);
    }

    [Fact]
    public async Task Saving_replaces_the_file_atomically_and_leaves_no_temporary_behind()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");

        await ConfigFile.SaveAsync(new WgFetchConfig { Scope = "user" }, path, CancellationToken.None);
        await ConfigFile.SaveAsync(new WgFetchConfig { Scope = "machine" }, path, CancellationToken.None);

        Assert.Equal("machine", (await ConfigFile.LoadAsync(path, CancellationToken.None)).Scope);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Saving_never_persists_credentials()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");

        await ConfigFile.SaveAsync(
            new WgFetchConfig { AiKey = "ai-secret", SearchKey = "search-secret", GithubToken = "github-secret" },
            path,
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(path, CancellationToken.None);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        var loaded = await ConfigFile.LoadAsync(path, CancellationToken.None);
        Assert.Null(loaded.AiKey);
        Assert.Null(loaded.SearchKey);
        Assert.Null(loaded.GithubToken);
    }
}

public sealed class ConfigRedactionTests
{
    private static WgFetchConfig WithSecrets() => new()
    {
        AiEndpoint = "https://ai.example/v1",
        AiKey = "ai-secret-value",
        SearchKey = "search-secret-value",
        GithubToken = "github-secret-value",
    };

    [Fact]
    public void Redacted_strips_every_secret_but_keeps_non_secret_context()
    {
        var redacted = WithSecrets().Redacted();

        Assert.Equal(SecretRedactor.Placeholder, redacted.AiKey);
        Assert.Equal(SecretRedactor.Placeholder, redacted.SearchKey);
        Assert.Equal(SecretRedactor.Placeholder, redacted.GithubToken);
        Assert.Equal("https://ai.example/v1", redacted.AiEndpoint);
    }

    public sealed class ConfigSettingsTests
    {
        [Fact]
        public void Setting_and_unsetting_known_values_preserves_the_remaining_configuration()
        {
            var original = new WgFetchConfig { Scope = "machine", AiKey = "secret" };

            Assert.True(ConfigSettings.TrySet(original, "parallelDownloads", "4", out var updated, out var error), error);
            Assert.Equal(4, updated.ParallelDownloads);
            Assert.Equal("machine", updated.Scope);

            Assert.True(ConfigSettings.TryUnset(updated, "parallelDownloads", out var unset, out error), error);
            Assert.Null(unset.ParallelDownloads);
            Assert.Equal("secret", unset.AiKey);
        }

        [Theory]
        [InlineData("threshold", "1.01")]
        [InlineData("keepVersions", "0")]
        [InlineData("parallelDownloads", "17")]
        [InlineData("maxPerHost", "0")]
        public void Rejects_out_of_range_numeric_values(string name, string value)
        {
            Assert.False(ConfigSettings.TrySet(new WgFetchConfig(), name, value, out _, out _));
        }

        [Fact]
        public void Normalizes_enum_like_values()
        {
            Assert.True(ConfigSettings.TrySet(new WgFetchConfig(), "scope", "Machine", out var scope, out _));
            Assert.True(ConfigSettings.TrySet(scope, "architecture", "X64", out var architecture, out _));
            Assert.Equal("machine", architecture.Scope);
            Assert.Equal("x64", architecture.Architecture);
        }

        [Fact]
        public void Rejects_unknown_search_provider_and_blank_directory()
        {
            Assert.False(ConfigSettings.TrySet(new WgFetchConfig(), "searchProvider", "typo", out _, out var providerError));
            Assert.Contains("searchProvider must be one of", providerError, StringComparison.Ordinal);

            Assert.False(ConfigSettings.TrySet(new WgFetchConfig(), "modelsRoot", "", out _, out var directoryError));
            Assert.Equal("directory path must not be blank.", directoryError);
        }

        [Theory]
        [InlineData("aiKey")]
        [InlineData("searchKey")]
        [InlineData("githubToken")]
        public void Rejects_credential_settings(string name)
        {
            Assert.False(ConfigSettings.TrySet(new WgFetchConfig(), name, "secret", out _, out _));
        }
    }

    [Fact]
    public void Redacted_leaves_absent_secrets_absent()
    {
        var redacted = new WgFetchConfig().Redacted();

        Assert.Null(redacted.AiKey);
        Assert.Null(redacted.SearchKey);
        Assert.Null(redacted.GithubToken);
    }

    [Fact]
    public void Secrets_enumerates_exactly_the_values_that_must_never_be_logged()
    {
        Assert.Equal(
            ["ai-secret-value", "search-secret-value", "github-secret-value"],
            WithSecrets().Secrets);

        Assert.Empty(new WgFetchConfig { AiKey = "   " }.Secrets);
    }

    [Fact]
    public void A_configured_secret_never_survives_redaction_of_arbitrary_text()
    {
        var config = WithSecrets();

        var redacted = SecretRedactor.Redact(
            "endpoint https://ai.example/v1 with key ai-secret-value and token github-secret-value",
            config.Secrets);

        Assert.DoesNotContain("ai-secret-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("github-secret-value", redacted, StringComparison.Ordinal);
    }
}

public sealed class WgFetchPathsTests
{
    [Fact]
    public void All_state_lives_under_a_single_root()
    {
        var root = WgFetchPaths.RootDirectory;

        Assert.EndsWith("wgfetch", root, StringComparison.Ordinal);
        Assert.StartsWith(root, WgFetchPaths.ModelsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(root, WgFetchPaths.SourceDirectory, StringComparison.Ordinal);
        Assert.StartsWith(root, WgFetchPaths.CacheDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_a_default_path_creates_nothing()
    {
        var root = WgFetchPaths.RootDirectory;
        var existedBefore = Directory.Exists(root);

        _ = ConfigFile.DefaultPath;

        Assert.Equal(existedBefore, Directory.Exists(root));
    }
}
