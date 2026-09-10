using WgFetch.Core.Progress;

namespace WgFetch.Core.Tests.Progress;

/// <summary>
/// Themed progress is a convenience, never a requirement: when output is piped, redirected, in CI, or
/// explicitly suppressed, the renderer degrades to plain lines with zero escape sequences
/// (docs/REQUIREMENTS.md, "Progress display").
/// </summary>
public sealed class TerminalCapabilityTests
{
    private static TerminalEnvironment Interactive() => new()
    {
        OutputRedirected = false,
        ErrorRedirected = false,
        Term = "xterm-256color",
        IsWindows = false,
    };

    [Fact]
    public void Interactive_terminals_get_the_rich_renderer() =>
        Assert.Equal(TerminalMode.Interactive, TerminalCapability.Detect(Interactive()));

    [Fact]
    public void Redirected_stdout_degrades_to_plain() =>
        Assert.Equal(TerminalMode.Plain, TerminalCapability.Detect(Interactive() with { OutputRedirected = true }));

    [Fact]
    public void Redirected_stderr_degrades_to_plain() =>
        Assert.Equal(TerminalMode.Plain, TerminalCapability.Detect(Interactive() with { ErrorRedirected = true }));

    [Fact]
    public void No_color_environment_variable_forces_plain() =>
        Assert.Equal(TerminalMode.Plain, TerminalCapability.Detect(Interactive() with { NoColorSet = true }));

    [Fact]
    public void Ci_forces_plain() =>
        Assert.Equal(TerminalMode.Plain, TerminalCapability.Detect(Interactive() with { CiSet = true }));

    [Theory]
    [InlineData("dumb")]
    [InlineData("DUMB")]
    public void Dumb_terminals_force_plain(string term) =>
        Assert.Equal(TerminalMode.Plain, TerminalCapability.Detect(Interactive() with { Term = term }));

    [Fact]
    public void A_missing_TERM_forces_plain_on_unix_but_not_on_windows()
    {
        Assert.Equal(TerminalMode.Plain, TerminalCapability.Detect(Interactive() with { Term = null }));
        Assert.Equal(TerminalMode.Interactive, TerminalCapability.Detect(Interactive() with { Term = null, IsWindows = true }));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Explicit_switches_force_plain(bool plain, bool noColor, bool json)
    {
        var environment = Interactive() with { PlainRequested = plain, NoColorRequested = noColor, JsonRequested = json };

        Assert.Equal(TerminalMode.Plain, TerminalCapability.Detect(environment));
    }

    [Fact]
    public void Detection_rejects_a_null_environment() =>
        Assert.Throws<ArgumentNullException>(() => TerminalCapability.Detect(null!));
}

public sealed class PlainProgressRendererTests
{
    private static string[] Lines(StringWriter buffer) =>
        buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Emits_readable_lines_with_no_escape_sequences_or_carriage_returns()
    {
        var buffer = new StringWriter();
        var renderer = new PlainProgressRenderer(buffer);

        renderer.StartTarget("nina");
        renderer.Update("nina", AcquisitionPhase.Tracking, "5.0/10.0 MiB", 0.5);
        renderer.Complete("nina", AcquisitionPhase.Done, "3.2.0.1001");
        await renderer.DisposeAsync();

        var output = buffer.ToString();
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\r', output);
        Assert.Contains("[nina] acquiring", output, StringComparison.Ordinal);
        Assert.Contains("[nina] acquired — 3.2.0.1001", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suppresses_repeated_identical_phase_lines_so_a_pipe_does_not_fill_with_noise()
    {
        var buffer = new StringWriter();
        var renderer = new PlainProgressRenderer(buffer);

        for (var i = 0; i < 10; i++)
        {
            renderer.Update("nina", AcquisitionPhase.Tracking);
        }

        await renderer.DisposeAsync();

        Assert.Single(Lines(buffer));
    }

    [Fact]
    public async Task Warnings_and_errors_always_survive_plain_mode()
    {
        var buffer = new StringWriter();
        var renderer = new PlainProgressRenderer(buffer);

        renderer.Warn("candidate rejected: content-type text/html");
        renderer.Error("no verified candidate for phd2");
        await renderer.DisposeAsync();

        var output = buffer.ToString();
        Assert.Contains("warning: candidate rejected: content-type text/html", output, StringComparison.Ordinal);
        Assert.Contains("error: no verified candidate for phd2", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_targets_do_not_interleave_within_a_line()
    {
        var buffer = new StringWriter();
        var renderer = new PlainProgressRenderer(buffer);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 32),
            (i, _) =>
            {
                renderer.Complete($"target{i}", AcquisitionPhase.Done, "1.0");
                return ValueTask.CompletedTask;
            });

        await renderer.DisposeAsync();

        var lines = Lines(buffer);
        Assert.Equal(32, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\[target\d+\] acquired — 1\.0\r?$", line));
    }
}

public sealed class PhaseVocabularyTests
{
    [Fact]
    public void Every_phase_has_a_plain_english_description()
    {
        foreach (var phase in Enum.GetValues<AcquisitionPhase>())
        {
            var description = PhaseVocabulary.Describe(phase);

            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.Equal(description, description.ToLowerInvariant());
        }
    }

    [Fact]
    public void An_ascii_spinner_exists_for_terminals_without_wide_glyphs()
    {
        Assert.NotEmpty(PhaseVocabulary.AsciiFrames);
        Assert.All(PhaseVocabulary.AsciiFrames, frame => Assert.True(frame.All(char.IsAscii)));
        Assert.NotEmpty(PhaseVocabulary.MoonFrames);
    }
}

public sealed class SessionReportTests
{
    [Fact]
    public void Summarises_a_run_in_one_line()
    {
        var report = new SessionReport
        {
            TargetsAcquired = 3,
            TargetsSkipped = 1,
            FramesRejected = 2,
            IntegrationTime = TimeSpan.FromSeconds(42.5),
            BytesAcquired = 10 * 1024 * 1024,
        };

        var rendered = report.Render();

        Assert.Contains("3 target(s) acquired", rendered, StringComparison.Ordinal);
        Assert.Contains("1 skipped", rendered, StringComparison.Ordinal);
        Assert.Contains("2 frame(s) rejected", rendered, StringComparison.Ordinal);
        Assert.Contains("10.0 MiB", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', rendered);
    }
}
