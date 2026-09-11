using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Cli;

/// <summary>Renders the friendly no-command landing view without changing command behavior.</summary>
public static class SplashScreen
{
    /// <summary>Writes the colorless fallback used when color has explicitly been disabled.</summary>
    public static void WritePlain(TextWriter writer, TargetsDocument? targets, string outputDirectory, bool prerequisitesInstalled)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("wgfetch");
        writer.WriteLine("resolve • verify • download");
        writer.WriteLine();
        WriteStatus(writer, targets, outputDirectory, prerequisitesInstalled);
    }

    /// <summary>Builds the colored interactive splash using Spectre.Console.</summary>
    public static IRenderable CreateInteractive(TargetsDocument? targets, string outputDirectory, bool prerequisitesInstalled)
    {
        var art = new Markup(
            "[#67e8f9]·[/]       [#6366f1]⋆[/]       [#67e8f9]·[/]          [#6366f1]✦[/]\n" +
            " [#6366f1]✦[/]      [#67e8f9]·[/]              [#67e8f9]/\\[/]\n" +
            "         [#67e8f9]⋆[/]             [#67e8f9]/  \\[/]       [#6366f1]⋆[/]\n" +
            "                    [#67e8f9]o======/    \\[/]\n" +
            "                           [#67e8f9](___)[/]\n" +
            "[#6366f1]██╗    ██╗ ██████╗ ███████╗███████╗████████╗ ██████╗██╗[/]\n" +
            "[#6366f1]██║    ██║██╔════╝ ██╔════╝██╔════╝╚══██╔══╝██╔════╝██║[/]\n" +
            "[#67e8f9]██║ █╗ ██║██║  ███╗█████╗  █████╗     ██║   ██║     ██║[/]\n" +
            "[#67e8f9]██║███╗██║██║   ██║██╔══╝  ██╔══╝     ██║   ██║     ██║[/]\n" +
            "[#6366f1]╚███╔███╔╝╚██████╔╝██║     ███████╗   ██║   ╚██████╗██║[/]\n" +
            "[#6366f1] ╚══╝╚══╝  ╚═════╝ ╚═╝     ╚══════╝   ╚═╝    ╚═════╝╚═╝[/]\n\n" +
            "[#67e8f9]          resolve  •  verify  •  download[/]");

        var panel = new Panel(art)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex("#6366f1"))
            .Padding(3, 1);

        var status = new StringWriter(CultureInfo.InvariantCulture);
        WriteStatus(status, targets, outputDirectory, prerequisitesInstalled);
        return new Rows(panel, new Text(status.ToString()));
    }

    private static void WriteStatus(TextWriter writer, TargetsDocument? targets, string outputDirectory, bool prerequisitesInstalled)
    {
        if (targets is null)
        {
            writer.WriteLine("Get started: run 'wgfetch prereqs install', then 'wgfetch add <app>'.");
            return;
        }

        var acquired = targets.Targets.Count(target => target.State == TargetState.Acquired);
        DateTimeOffset? lastFetch = targets.Targets
            .Select(target => target.LastAttempt)
            .Max();

        writer.WriteLine($"Targets acquired  ·  {acquired}       Prereqs  ·  {(prerequisitesInstalled ? "installed" : "not installed")}");
        writer.WriteLine($"Source tree       ·  {outputDirectory}  Last fetch  ·  {(lastFetch is null ? "never" : FormatElapsed(lastFetch.Value))}");
    }

    private static string FormatElapsed(DateTimeOffset time)
    {
        var elapsed = DateTimeOffset.UtcNow - time;
        if (elapsed <= TimeSpan.FromHours(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return $"{hours} {(hours == 1 ? "hour" : "hours")} ago";
        }

        var days = (int)elapsed.TotalDays;
        return $"{days} {(days == 1 ? "day" : "days")} ago";
    }
}
