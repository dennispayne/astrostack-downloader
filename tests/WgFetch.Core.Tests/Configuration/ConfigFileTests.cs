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
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Saving_uses_owner_only_permissions_on_Unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = temp.Combine("nested", "config.json");

        await ConfigFile.SaveAsync(new WgFetchConfig { AiKey = "secret" }, path, CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task Saving_to_an_existing_custom_directory_preserves_its_Unix_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var directory = temp.Combine("shared");
        Directory.CreateDirectory(directory);
        const UnixFileMode mode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(directory, mode);

        await ConfigFile.SaveAsync(
            new WgFetchConfig { AiKey = "secret" },
            Path.Combine(directory, "config.json"),
            CancellationToken.None);

        Assert.Equal(mode, File.GetUnixFileMode(directory));
    }

    [Fact]
    public async Task Update_waits_for_an_external_file_lock()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");
        await ConfigFile.SaveAsync(new WgFetchConfig { Scope = "user" }, path, CancellationToken.None);
        await using var externalLock = new FileStream(
            path + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var update = ConfigFile.TryUpdateAsync(
            path,
            config => config with { LogLevel = "debug" },
            CancellationToken.None);
        await Task.Delay(75);
        Assert.False(update.IsCompleted);

        await externalLock.DisposeAsync();
        var updated = await update;

        Assert.Equal("user", updated.Scope);
        Assert.Equal("debug", updated.LogLevel);
    }

    [Fact]
    public async Task Update_propagates_a_read_failure_instead_of_replacing_the_file()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");
        await ConfigFile.SaveAsync(
            new WgFetchConfig { Scope = "user", AiKey = "ai-secret" },
            path,
            CancellationToken.None);

        // Hold the file exclusively so the read inside the update fails: File.Exists also reports
        // false for an unreadable file, which must never be mistaken for a missing one.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => ConfigFile.TryUpdateAsync(
                path,
                config => config with { LogLevel = "debug" },
                CancellationToken.None));
        }

        var reloaded = await ConfigFile.LoadAsync(path, CancellationToken.None);
        Assert.Equal("user", reloaded.Scope);
        Assert.Equal("ai-secret", reloaded.AiKey);
        Assert.Null(reloaded.LogLevel);
    }

    [Fact]
    public async Task Saving_persists_credentials()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");

        await ConfigFile.SaveAsync(
            new WgFetchConfig { AiKey = "ai-secret", SearchKey = "search-secret", GithubToken = "github-secret" },
            path,
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(path, CancellationToken.None);
        Assert.Contains("ai-secret", json, StringComparison.Ordinal);
        var loaded = await ConfigFile.LoadAsync(path, CancellationToken.None);
        Assert.Equal("ai-secret", loaded.AiKey);
        Assert.Equal("search-secret", loaded.SearchKey);
        Assert.Equal("github-secret", loaded.GithubToken);
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

    [Fact]
    public void Redacted_scrubs_a_secret_embedded_in_an_endpoint_url()
    {
        var config = new WgFetchConfig
        {
            AiEndpoint = "https://ai.example/v1?api_key=super-secret-ai-key",
            SearchEndpoint = "https://search.example/v1?api_key=super-secret-search-key",
        };

        var redacted = config.Redacted();

        Assert.DoesNotContain("super-secret-ai-key", redacted.AiEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-search-key", redacted.SearchEndpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_scrubs_a_configured_secret_embedded_under_an_unknown_url_key()
    {
        var secret = "custom-secret-value";
        var config = new WgFetchConfig
        {
            AiKey = secret,
            AiEndpoint = $"https://ai.example/v1?custom_token={secret}",
            SearchEndpoint = $"https://search.example/{secret}/v1",
        };

        var redacted = config.Redacted();

        Assert.DoesNotContain(secret, redacted.AiEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted.SearchEndpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_scrubs_a_configured_secret_that_is_form_encoded_with_a_plus_for_space()
    {
        const string secret = "a b";
        var config = new WgFetchConfig
        {
            AiKey = secret,
            AiEndpoint = "https://ai.example/v1?foo=a+b",
        };

        var redacted = config.Redacted();

        Assert.DoesNotContain(secret, redacted.AiEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("a+b", redacted.AiEndpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_scrubs_a_configured_secret_percent_encoded_in_endpoint_path_and_fragment()
    {
        const string secret = "abc";
        var config = new WgFetchConfig
        {
            AiKey = secret,
            AiEndpoint = "https://ai.example/v1/%61%62%63#%61%62%63",
        };

        var redacted = config.Redacted();

        Assert.DoesNotContain("%61%62%63", redacted.AiEndpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, redacted.AiEndpoint, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted.AiEndpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_scrubs_even_a_short_configured_secret_from_every_string_field()
    {
        const string secret = "abc";
        var config = new WgFetchConfig
        {
            AiKey = secret,
            OutputDirectory = $"/srv/{secret}/source",
            AiEndpoint = $"https://ai.example/{secret}",
            AiModel = $"model-{secret}",
        };

        var redacted = config.Redacted();

        Assert.DoesNotContain(secret, redacted.OutputDirectory, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted.AiEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted.AiModel, StringComparison.Ordinal);
        Assert.Equal(SecretRedactor.Placeholder, redacted.AiKey);
    }

    [Fact]
    public void Redacted_scrubs_form_encoded_configured_secret_from_non_url_fields()
    {
        const string secret = "a b";
        var config = new WgFetchConfig
        {
            AiKey = secret,
            OutputDirectory = "/tmp/a+b/source",
        };

        var redacted = config.Redacted();

        Assert.DoesNotContain("a+b", redacted.OutputDirectory, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted.OutputDirectory, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted.OutputDirectory, StringComparison.Ordinal);
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
            Assert.Equal("modelsRoot must not be blank.", directoryError);
        }

        [Theory]
        [InlineData("trace")]
        [InlineData("debug")]
        [InlineData("info")]
        [InlineData("warn")]
        [InlineData("error")]
        [InlineData("none")]
        public void Accepts_every_known_log_level(string value)
        {
            Assert.True(ConfigSettings.TrySet(new WgFetchConfig(), "logLevel", value, out var updated, out var error), error);
            Assert.Equal(value, updated.LogLevel);
        }

        [Fact]
        public void Rejects_an_unknown_log_level()
        {
            Assert.False(ConfigSettings.TrySet(new WgFetchConfig(), "logLevel", "verbose", out _, out var error));
            Assert.Contains("logLevel must be one of", error, StringComparison.Ordinal);
            Assert.Contains("information", error, StringComparison.Ordinal);
            Assert.Contains("warning", error, StringComparison.Ordinal);
            Assert.Contains("off", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("aiKey")]
        [InlineData("searchKey")]
        [InlineData("githubToken")]
        public void Accepts_credential_settings(string name)
        {
            Assert.True(ConfigSettings.TrySet(new WgFetchConfig(), name, "secret", out var updated, out var error), error);
            Assert.Equal(SecretRedactor.Placeholder, ConfigSettings.GetRedactedValue(updated, name, out _));
        }

        [Theory]
        [InlineData("aiEndpoint")]
        [InlineData("searchEndpoint")]
        public void Endpoint_accessors_redact_embedded_credentials(string name)
        {
            var config = new WgFetchConfig
            {
                AiEndpoint = "https://user:" + "password" + "@ai.example/v1?api_key=secret",
                SearchEndpoint = "https://user:" + "password" + "@search.example/v1?api_key=secret",
            };

            var value = ConfigSettings.GetRedactedValue(config, name, out var error);

            Assert.Null(error);
            Assert.DoesNotContain("password", value, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", value, StringComparison.Ordinal);
            Assert.Contains(SecretRedactor.Placeholder, value, StringComparison.Ordinal);
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
    public void Redacted_removes_token_shaped_values_from_endpoints()
    {
        var config = new WgFetchConfig
        {
            AiEndpoint = "https://ai.example/v1/sk-abcdefghijklmnop123",
            SearchEndpoint = "https://search.example/v1?project=ghp_abcdefghijklmnop123",
        }.Redacted();

        Assert.DoesNotContain("sk-abcdefghijklmnop123", config.AiEndpoint!, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_abcdefghijklmnop123", config.SearchEndpoint!, StringComparison.Ordinal);
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
