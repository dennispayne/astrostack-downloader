using WgFetch.Core.Logging;

namespace WgFetch.Core.Tests.Logging;

/// <summary>
/// A known key or token value must never appear in any log at any verbosity, in provenance, in
/// <c>--json</c> output, or in a diagnostics bundle (docs/REQUIREMENTS.md, "Privacy").
/// </summary>
public sealed class SecretRedactorTests
{
    // Synthetic values assembled at runtime so no literal that looks like a credential is committed.
    private static readonly string GitHubToken = "gh" + "p_" + new string('A', 36);
    private static readonly string OpenAiKey = "sk" + "-proj-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123";

    [Fact]
    public void Redacts_github_tokens_anywhere_in_a_message() =>
        Assert.DoesNotContain(GitHubToken, SecretRedactor.Redact($"calling api with {GitHubToken} now"), StringComparison.Ordinal);

    [Fact]
    public void Redacts_openai_style_keys() =>
        Assert.DoesNotContain(OpenAiKey, SecretRedactor.Redact($"Authorization header uses {OpenAiKey}"), StringComparison.Ordinal);

    [Fact]
    public void Redacts_bearer_headers()
    {
        var opaque = "aVeryLongOpaqueValue123456";
        var redacted = SecretRedactor.Redact("Authorization: Bea" + "rer " + opaque);

        Assert.DoesNotContain(opaque, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_explicitly_registered_secret_values()
    {
        var redacted = SecretRedactor.Redact("provider configured with hunter2-secret-value", ["hunter2-secret-value"]);

        Assert.DoesNotContain("hunter2-secret-value", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.search.example/search?q=nina&key=supersecretvalue", "supersecretvalue")]
    [InlineData("https://api.example/v1?access_token=abcdef123456", "abcdef123456")]
    [InlineData("https://cdn.example/asset?X-Amz-Signature-Version=AWS4-HMAC-SHA256", "AWS4-HMAC-SHA256")]
    public void Redacts_credentials_and_sensitive_query_parameters_in_urls(string url, string secret)
    {
        var redacted = SecretRedactor.RedactUrl(url);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_credentials_embedded_in_the_userinfo_of_a_url()
    {
        var url = "https://user:" + "hunter2" + "@vendor.example/setup.exe";

        var redacted = SecretRedactor.RedactUrl(url);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_ordinary_urls_untouched()
    {
        const string url = "https://github.com/isbeorn/nina/releases/download/3.2/NINASetup.exe";

        Assert.Equal(url, SecretRedactor.RedactUrl(url));
    }

    [Fact]
    public void Redacts_urls_embedded_in_free_text()
    {
        var redacted = SecretRedactor.Redact("searching via https://searx.example/search?q=nina&key=topsecretkey now");

        Assert.DoesNotContain("topsecretkey", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Recognises_sensitive_configuration_field_names()
    {
        Assert.True(SecretRedactor.IsSensitiveFieldName("githubToken"));
        Assert.True(SecretRedactor.IsSensitiveFieldName("aiKey"));
        Assert.False(SecretRedactor.IsSensitiveFieldName("aiEndpoint"));
    }

    [Theory]
    [InlineData("X-Auth")]
    [InlineData("X-Authorization")]
    public void Recognises_authentication_headers_as_sensitive(string name) =>
        Assert.True(SecretRedactor.IsSensitiveHeaderName(name));

    [Fact]
    public void Handles_null_and_empty_input()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }
}

public sealed class JsonEventWriterTests
{
    [Fact]
    public void Redacts_secrets_from_every_emitted_event()
    {
        var buffer = new StringWriter();
        var writer = new JsonEventWriter(buffer, ["topsecretkey"]);

        writer.Write(new JsonEvent
        {
            Event = "candidate.rejected",
            Target = "nina",
            Message = "search used key topsecretkey",
            Url = "https://searx.example/search?q=nina&key=topsecretkey",
        });
        writer.Flush();

        var output = buffer.ToString();
        Assert.DoesNotContain("topsecretkey", output, StringComparison.Ordinal);
        Assert.Contains("candidate.rejected", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Deterministic_mode_orders_events_independently_of_completion_order()
    {
        var first = Render(["zeta", "alpha", "mu"]);
        var second = Render(["mu", "zeta", "alpha"]);

        Assert.Equal(first, second);

        static string Render(IEnumerable<string> targets)
        {
            var buffer = new StringWriter();
            var writer = new JsonEventWriter(buffer, deterministicOrder: true);
            foreach (var target in targets)
            {
                writer.Write(new JsonEvent { Event = "target.acquired", Target = target });
            }

            writer.Flush();
            return buffer.ToString();
        }
    }

    [Fact]
    public void Emits_newline_delimited_json_without_escape_sequences()
    {
        var buffer = new StringWriter();
        var writer = new JsonEventWriter(buffer);

        writer.Write(new JsonEvent { Event = "run.started" });
        writer.Flush();

        var output = buffer.ToString();
        Assert.DoesNotContain('\u001b', output);
        Assert.StartsWith("{", output.Trim(), StringComparison.Ordinal);
    }
}

public sealed class PhaseTimingsTests
{
    [Fact]
    public void Records_and_summarises_phases_deterministically()
    {
        var timings = new PhaseTimings();
        timings.Record("download", TimeSpan.FromMilliseconds(120));
        timings.Record("download", TimeSpan.FromMilliseconds(80));
        timings.Record("verify", TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.FromMilliseconds(200), timings.Totals["download"]);
        var summary = timings.Summarize();
        Assert.Contains("download: 200ms across 2 call(s)", summary, StringComparison.Ordinal);
        Assert.True(summary.IndexOf("download", StringComparison.Ordinal) < summary.IndexOf("verify", StringComparison.Ordinal));
    }

    [Fact]
    public void Measure_scope_records_elapsed_time()
    {
        var timings = new PhaseTimings();
        using (timings.Measure("inference"))
        {
            Thread.Sleep(5);
        }

        Assert.True(timings.Totals["inference"] > TimeSpan.Zero);
    }
}

public sealed class LogLevelParserTests
{
    [Theory]
    [InlineData("trace", Microsoft.Extensions.Logging.LogLevel.Trace)]
    [InlineData("debug", Microsoft.Extensions.Logging.LogLevel.Debug)]
    [InlineData("info", Microsoft.Extensions.Logging.LogLevel.Information)]
    [InlineData(null, Microsoft.Extensions.Logging.LogLevel.Information)]
    [InlineData("warn", Microsoft.Extensions.Logging.LogLevel.Warning)]
    [InlineData("error", Microsoft.Extensions.Logging.LogLevel.Error)]
    [InlineData("none", Microsoft.Extensions.Logging.LogLevel.None)]
    public void Parses_documented_levels(string? value, Microsoft.Extensions.Logging.LogLevel expected) =>
        Assert.Equal(expected, LogLevelParser.Parse(value));

    [Fact]
    public void Rejects_unknown_levels() => Assert.False(LogLevelParser.IsValid("chatty"));
}

public sealed class RedactingLoggerTests
{
    [Fact]
    public void Never_writes_a_secret_even_at_trace_verbosity()
    {
        var buffer = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(buffer, Microsoft.Extensions.Logging.LogLevel.Trace, ["topsecretkey"]);
        var logger = provider.CreateLogger("test");

        logger.Log(
            Microsoft.Extensions.Logging.LogLevel.Trace,
            default,
            "raw completion mentioning topsecretkey",
            null,
            (state, _) => state);

        Assert.DoesNotContain("topsecretkey", buffer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Honours_the_minimum_level()
    {
        var buffer = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(buffer, Microsoft.Extensions.Logging.LogLevel.Warning);
        var logger = provider.CreateLogger("test");

        logger.Log(Microsoft.Extensions.Logging.LogLevel.Debug, default, "chatter", null, (state, _) => state);

        Assert.Equal(string.Empty, buffer.ToString());
    }
}
