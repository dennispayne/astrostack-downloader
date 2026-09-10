using WgFetch.Core.Cli;

namespace WgFetch.Core.Tests.Cli;

/// <summary>
/// Parsing performs no I/O and loads no model, so <c>--help</c>, <c>--version</c> and argument errors
/// stay fast (docs/REQUIREMENTS.md, "Responsiveness" and "CLI surface").
/// </summary>
public sealed class CommandLineParserTests
{
    [Fact]
    public void Parses_positional_names_and_options()
    {
        var parsed = CommandLineParser.Parse(["fetch", "nina", "phd2", "--output", "/tmp/source", "--dry-run"]);

        Assert.False(parsed.HasErrors);
        Assert.Equal("fetch", parsed.Command);
        Assert.Equal(["nina", "phd2"], parsed.Positional);
        Assert.Equal("/tmp/source", parsed.Value("--output"));
        Assert.True(parsed.Has("--dry-run"));
    }

    [Fact]
    public void Supports_equals_syntax()
    {
        var parsed = CommandLineParser.Parse(["fetch", "--threshold=0.72"]);

        Assert.False(parsed.HasErrors);
        Assert.Equal(0.72, parsed.DoubleValue("--threshold"));
    }

    [Fact]
    public void Rejects_unknown_commands()
    {
        var parsed = CommandLineParser.Parse(["frobnicate"]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("unknown command", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_unknown_options()
    {
        var parsed = CommandLineParser.Parse(["fetch", "--turbo"]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("unknown option", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_value_option_without_a_value()
    {
        var parsed = CommandLineParser.Parse(["fetch", "--output"]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("requires a value", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_value_given_to_a_flag()
    {
        var parsed = CommandLineParser.Parse(["fetch", "--dry-run=yes"]);

        Assert.True(parsed.HasErrors);
    }

    [Fact]
    public void Requires_a_subcommand_where_the_spec_defines_one()
    {
        Assert.True(CommandLineParser.Parse(["prereqs"]).HasErrors);
        Assert.False(CommandLineParser.Parse(["prereqs", "install"]).HasErrors);
        Assert.Equal("install", CommandLineParser.Parse(["prereqs", "install"]).SubCommand);
        Assert.True(CommandLineParser.Parse(["prereqs", "reinstall"]).HasErrors);
    }

    [Fact]
    public void Verbose_implies_debug_logging()
    {
        var parsed = CommandLineParser.Parse(["fetch", "nina", "--verbose"]);

        Assert.Equal("debug", parsed.Value("--log-level"));
    }

    [Fact]
    public void Explicit_log_level_wins_over_verbose()
    {
        var parsed = CommandLineParser.Parse(["fetch", "nina", "--verbose", "--log-level", "trace"]);

        Assert.Equal("trace", parsed.Value("--log-level"));
    }

    [Fact]
    public void Help_and_version_are_recognised_without_a_command()
    {
        Assert.True(CommandLineParser.Parse(["--help"]).HelpRequested);
        Assert.False(CommandLineParser.Parse(["--help"]).HasErrors);
        Assert.True(CommandLineParser.Parse(["--version"]).VersionRequested);
        Assert.False(CommandLineParser.Parse(["--version"]).HasErrors);
    }

    [Fact]
    public void No_command_is_a_usage_error() => Assert.True(CommandLineParser.Parse([]).HasErrors);

    [Fact]
    public void Double_dash_ends_option_parsing()
    {
        var parsed = CommandLineParser.Parse(["add", "--", "--weird-name"]);

        Assert.False(parsed.HasErrors);
        Assert.Equal(["--weird-name"], parsed.Positional);
    }

    [Fact]
    public void Every_documented_command_is_present()
    {
        string[] expected =
        [
            "fetch", "resolve", "add", "remove", "status", "export", "import", "refresh", "prereqs",
            "verify", "recipes", "list", "diagnostics",
        ];

        Assert.Equal(expected.Order(), CommandLineParser.Commands.Select(c => c.Name).Order());
    }

    [Fact]
    public void Fetch_accepts_every_documented_option()
    {
        string[] documented =
        [
            "--winget-repo", "--from-file", "--output", "--installer-dir", "--cache-dir", "--only-missing",
            "--refresh-stale", "--embedding-model", "--llm-model", "--models-root", "--recipe", "--recipe-inline",
            "--arch", "--scope", "--threshold", "--keep-versions", "--require-hash-match", "--ai-mode",
            "--ai-endpoint", "--ai-model", "--ai-key", "--github-token", "--parallel-downloads", "--max-per-host",
            "--search-provider", "--search-endpoint", "--search-key", "--log-level", "--log-file", "--no-color",
            "--plain", "--no-resume", "--download-prereqs", "--dry-run", "--json",
        ];

        var fetch = CommandLineParser.FindCommand("fetch")!;
        foreach (var option in documented)
        {
            Assert.True(fetch.Accepts(option), $"fetch should accept {option}");
        }
    }

    [Fact]
    public void Json_is_available_on_every_command()
    {
        foreach (var command in CommandLineParser.Commands)
        {
            Assert.True(command.Accepts("--json"), $"{command.Name} should accept --json");
            Assert.True(command.Accepts("--verbose"), $"{command.Name} should accept --verbose");
        }
    }

    [Fact]
    public void Help_text_lists_commands_and_mentions_the_privacy_stance()
    {
        var help = CommandLineParser.RenderHelp();

        Assert.Contains("Usage: wgfetch <command> [options]", help, StringComparison.Ordinal);
        Assert.All(CommandLineParser.Commands, c => Assert.Contains(c.Name, help, StringComparison.Ordinal));
        Assert.Contains("no telemetry", help, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', help);
    }

    [Fact]
    public void Command_help_lists_that_command_s_options()
    {
        var help = CommandLineParser.RenderHelp("prereqs");

        Assert.Contains("--include-llm", help, StringComparison.Ordinal);
        Assert.Contains("install|status", help, StringComparison.Ordinal);
    }
}
