using Spectre.Console;
using WgFetch.Core.Cli;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Tests.Cli;

public sealed class SplashScreenTests
{
    [Fact]
    public void Plain_FirstRun_ShowsGetStartedHintWithoutBoxDrawing()
    {
        var output = new StringWriter();

        SplashScreen.WritePlain(output, null, "/source", prerequisitesPresent: false);

        Assert.Contains("wgfetch", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("prereqs install", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("╭", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_ExistingTargets_ShowsStatusInsteadOfFirstRunHint()
    {
        var output = new StringWriter();
        var targets = new TargetsDocument
        {
            Targets =
            [
                new TargetEntry { Name = "nina", State = TargetState.Acquired, LastAttempt = DateTimeOffset.UtcNow },
                new TargetEntry { Name = "phd2", State = TargetState.Listed },
            ],
        };

        SplashScreen.WritePlain(
            output,
            targets,
            "/source",
            prerequisitesPresent: true,
            now: new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains("Targets acquired", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("·  1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Prereqs", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("present (unverified)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("/source", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_StaleTargets_CountAsAcquired()
    {
        var output = new StringWriter();
        var targets = new TargetsDocument
        {
            Targets =
            [
                new TargetEntry { Name = "nina", State = TargetState.Acquired },
                new TargetEntry { Name = "phd2", State = TargetState.Stale },
                new TargetEntry { Name = "astap", State = TargetState.Listed },
            ],
        };

        SplashScreen.WritePlain(output, targets, "/source", prerequisitesPresent: false);

        Assert.Contains("Targets acquired", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("·  2", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(90, "1 hour ago")]
    [InlineData(180, "3 hours ago")]
    [InlineData(1440, "1 day ago")]
    [InlineData(2880, "2 days ago")]
    public void Plain_LastAttempt_UsesDeterministicElapsedTime(int elapsedMinutes, string expected)
    {
        var output = new StringWriter();
        var now = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var targets = new TargetsDocument
        {
            Targets = [new TargetEntry { Name = "nina", LastAttempt = now.AddMinutes(-elapsedMinutes) }],
        };

        SplashScreen.WritePlain(output, targets, "/source", prerequisitesPresent: true, now);

        Assert.Contains($"Last attempt       ·  {expected}", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_EmptyTargets_ShowsStatusWithoutThrowing()
    {
        var output = new StringWriter();

        SplashScreen.WritePlain(
            output,
            new TargetsDocument(),
            "/source",
            prerequisitesPresent: false,
            now: new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains("Targets acquired", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("·  0", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Last attempt", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("never", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_TargetsError_ShowsErrorInsteadOfFirstRunHint()
    {
        var output = new StringWriter();

        SplashScreen.WritePlain(
            output,
            targets: null,
            "/source",
            prerequisitesPresent: false,
            targetsError: "unable to read targets.yaml");

        Assert.Contains("unable to read targets.yaml", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("/source", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Interactive_UsesGradientColorsAndRoundedBorder()
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(output),
        });

        console.Write(SplashScreen.CreateInteractive(null, "/source", prerequisitesPresent: false));

        Assert.Contains("╭", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("resolve - verify - download", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;99;102;241m", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Interactive_UsesAsciiArtThatFitsANarrowTerminal()
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = 60;

        console.Write(SplashScreen.CreateInteractive(null, "/source", prerequisitesPresent: false));

        var rendered = output.ToString();
        Assert.Contains("W   W  GGG  FFFFF", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('█', rendered);
        Assert.DoesNotContain('╔', rendered);
        Assert.DoesNotContain('•', rendered);
    }

    [Fact]
    public void Plain_SourceTreeWithControlCharacters_EmitsNoEscapeSequences()
    {
        var output = new StringWriter();

        SplashScreen.WritePlain(
            output,
            new TargetsDocument(),
            "/source\u001b[31mred\u0007",
            prerequisitesPresent: false,
            now: new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));

        var rendered = output.ToString();
        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", rendered, StringComparison.Ordinal);
        Assert.Contains("/source?[31mred?", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Interactive_SourceTreeWithControlCharacters_EmitsNoInjectedEscapeSequences()
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });

        console.Write(SplashScreen.CreateInteractive(
            new TargetsDocument(),
            "/source\u001b[31mred",
            prerequisitesPresent: false,
            new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero)));

        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
    }
}
