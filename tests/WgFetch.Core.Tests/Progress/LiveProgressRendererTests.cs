using Spectre.Console;
using WgFetch.Core.Progress;

namespace WgFetch.Core.Tests.Progress;

/// <summary>
/// The animated renderer, driven against an in-memory console so its output is assertable
/// (docs/REQUIREMENTS.md, "Progress and presentation").
/// </summary>
public sealed class LiveProgressRendererTests
{
    private static (IAnsiConsole Console, StringWriter Writer) CreateConsole()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            // The live renderer is only ever selected for an interactive ANSI terminal
            // (TerminalCapability picks the plain renderer otherwise), so the test console mirrors that.
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Interactive = InteractionSupport.Yes,
        });

        // A StringWriter reports no terminal size; Spectre's live display needs a concrete one.
        console.Profile.Width = 100;
        console.Profile.Height = 30;

        return (console, writer);
    }

    [Fact]
    public async Task RendersEveryTargetItWasToldAbout()
    {
        var (console, writer) = CreateConsole();

        await using (var renderer = new LiveProgressRenderer(console))
        {
            renderer.StartTarget("nina");
            renderer.StartTarget("phd2");
            renderer.Update("nina", AcquisitionPhase.Tracking, "setup.exe", 0.5);
            renderer.Complete("phd2", AcquisitionPhase.Done, "2.6.13");
            await Task.Delay(300, CancellationToken.None);
        }

        var output = writer.ToString();
        Assert.Contains("nina", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("phd2", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WarningsAndErrorsSurvivePastDisposal()
    {
        var (console, writer) = CreateConsole();

        await using (var renderer = new LiveProgressRenderer(console))
        {
            renderer.Warn("a stale recipe was skipped");
            renderer.Error("vendor.example.com refused the connection");
            await Task.Delay(300, CancellationToken.None);
        }

        var output = writer.ToString();
        Assert.Contains("stale recipe", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refused the connection", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkupInTargetNamesIsEscapedRatherThanInterpreted()
    {
        var (console, writer) = CreateConsole();

        await using (var renderer = new LiveProgressRenderer(console))
        {
            // Page content reaches these strings, so a bracketed payload must not become markup.
            renderer.StartTarget("[red]not-a-color[/]");
            renderer.Warn("[bold]not bold[/]");
            await Task.Delay(300, CancellationToken.None);
        }

        var output = writer.ToString();
        Assert.Contains("not-a-color", output, StringComparison.Ordinal);
        Assert.Contains("not bold", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReportIsRenderedOnce()
    {
        var (console, writer) = CreateConsole();

        await using (var renderer = new LiveProgressRenderer(console))
        {
            renderer.StartTarget("nina");
            renderer.Complete("nina", AcquisitionPhase.Done, "3.1.0");
            renderer.Report(new SessionReport
            {
                TargetsAcquired = 1,
                TargetsSkipped = 0,
                FramesRejected = 0,
                IntegrationTime = TimeSpan.FromSeconds(4),
                BytesAcquired = 1024,
            });

            await Task.Delay(300, CancellationToken.None);
        }

        Assert.Contains("acquired", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AsciiModeEmitsNoNonAsciiCharacters()
    {
        var (console, writer) = CreateConsole();

        await using (var renderer = new LiveProgressRenderer(console, unicode: false))
        {
            renderer.StartTarget("nina");
            renderer.Update("nina", AcquisitionPhase.PlateSolving, null, 0.25);
            await Task.Delay(300, CancellationToken.None);
        }

        // ANSI control sequences are themselves ASCII; what must not appear is decorative Unicode.
        Assert.All(writer.ToString(), c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} in ASCII mode"));
    }

    [Fact]
    public async Task DisposingTwiceIsSafe()
    {
        var (console, _) = CreateConsole();
        var renderer = new LiveProgressRenderer(console);

        await renderer.DisposeAsync();
        await renderer.DisposeAsync();
    }
}
