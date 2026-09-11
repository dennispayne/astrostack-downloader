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

        SplashScreen.WritePlain(output, null, "/source", prerequisitesInstalled: false);

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

        SplashScreen.WritePlain(output, targets, "/source", prerequisitesInstalled: true);

        Assert.Contains("Targets acquired  ·  1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Prereqs  ·  installed", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("/source", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Get started", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Interactive_UsesGradientColorsAndRoundedBorder()
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(output),
        });

        console.Write(SplashScreen.CreateInteractive(null, "/source", prerequisitesInstalled: false));

        Assert.Contains("\u001b[38;2;99;102;241m", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;103;232;249m", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("╭", output.ToString(), StringComparison.Ordinal);
    }
}
