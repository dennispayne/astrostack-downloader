using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Cli;

/// <summary>Renders the friendly no-command landing view without changing command behavior.</summary>
public static class SplashScreen
{
    /// <summary>Writes the colorless fallback used when color has explicitly been disabled.</summary>
    public static void WritePlain(
        TextWriter writer,
        TargetsDocument? targets,
        string outputDirectory,
        bool prerequisitesPresent,
        DateTimeOffset? now = null,
        string? targetsError = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        WritePlainHeader(writer);
        WriteStatus(writer, targets, outputDirectory, prerequisitesPresent, now ?? DateTimeOffset.UtcNow, targetsError);
    }

    /// <summary>Builds the colored interactive splash using Spectre.Console.</summary>
    public static IRenderable CreateInteractive(
        TargetsDocument? targets,
        string outputDirectory,
        bool prerequisitesPresent,
        DateTimeOffset? now = null,
        string? targetsError = null)
    {
        return new Rows(
            CreateInteractiveHeader(),
            CreateStatus(targets, outputDirectory, prerequisitesPresent, now ?? DateTimeOffset.UtcNow, targetsError));
    }

    internal static void WritePlainHeader(TextWriter writer, bool includeTitle = true)
    {
        if (includeTitle)
        {
            writer.WriteLine("wgfetch");
        }

        writer.WriteLine("resolve • verify • download");
        writer.WriteLine();
    }

    internal static IRenderable CreateInteractiveHeader()
    {
        // The rocket's roof is drawn with literal backslashes right up against a closing "[/]" tag.
        // Spectre's markup grammar only treats "[" and "]" specially (escaped as "[[" / "]]"), so a
        // bare backslash needs no escaping here and does not break the adjacent closing tag; the named
        // constant below just keeps the three occurrences readable rather than inline C# string escapes.
        const string backslash = "\\";
        var art = new Markup(
            "[#67e8f9]·[/]       [#6366f1]⋆[/]       [#67e8f9]·[/]          [#6366f1]✦[/]\n" +
            " [#6366f1]✦[/]      [#67e8f9]·[/]              [#67e8f9]/" + backslash + "[/]\n" +
            "         [#67e8f9]⋆[/]             [#67e8f9]/  " + backslash + "[/]       [#6366f1]⋆[/]\n" +
            "                    [#67e8f9]o======/    " + backslash + "[/]\n" +
            "                           [#67e8f9](___)[/]\n" +
            "[#6366f1]██╗    ██╗ ██████╗ ███████╗███████╗████████╗ ██████╗██╗[/]\n" +
            "[#6366f1]██║    ██║██╔════╝ ██╔════╝██╔════╝╚══██╔══╝██╔════╝██║[/]\n" +
            "[#67e8f9]██║ █╗ ██║██║  ███╗█████╗  █████╗     ██║   ██║     ██║[/]\n" +
            "[#67e8f9]██║███╗██║██║   ██║██╔══╝  ██╔══╝     ██║   ██║     ██║[/]\n" +
            "[#6366f1]╚███╔███╔╝╚██████╔╝██║     ███████╗   ██║   ╚██████╗██║[/]\n" +
            "[#6366f1] ╚══╝╚══╝  ╚═════╝ ╚═╝     ╚══════╝   ╚═╝    ╚═════╝╚═╝[/]\n\n" +
            "[#67e8f9]          resolve  •  verify  •  download[/]");

        return new Panel(art)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex("#6366f1"))
            .Padding(3, 1);
    }

    internal static IRenderable CreateStatus(
        TargetsDocument? targets,
        string outputDirectory,
        bool prerequisitesPresent,
        DateTimeOffset now,
        string? targetsError)
    {
        var status = new StringWriter(CultureInfo.InvariantCulture);
        WriteStatus(status, targets, outputDirectory, prerequisitesPresent, now, targetsError);
        return new Text(status.ToString());
    }

    internal static void WriteStatus(
        TextWriter writer,
        TargetsDocument? targets,
        string outputDirectory,
        bool prerequisitesPresent,
        DateTimeOffset now,
        string? targetsError)
    {
        // `prerequisitesPresent` comes from a hashing-free probe, so it may not claim "installed":
        // a same-size tampered asset passes the probe but fails digest verification. Say what is
        // actually known and point at the command that does verify.
        var prereqsLine = FormatLine("Prereqs", prerequisitesPresent ? "present (unverified)" : "not installed");

        if (targetsError is not null)
        {
            writer.WriteLine(FormatLine("Targets", targetsError));
            writer.WriteLine(prereqsLine);
            writer.WriteLine(FormatLine("Source tree", outputDirectory));
            return;
        }

        if (targets is null)
        {
            writer.WriteLine("Get started: run 'wgfetch prereqs install', then 'wgfetch add <app>'.");
            return;
        }

        var acquired = targets.Targets.Count(target => target.State is TargetState.Acquired or TargetState.Stale);
        DateTimeOffset? lastAttempt = targets.Targets.Count == 0
            ? null
            : targets.Targets.Select(target => target.LastAttempt).Max();

        writer.WriteLine($"{FormatLine("Targets acquired", acquired.ToString(CultureInfo.InvariantCulture))} {prereqsLine}");
        writer.WriteLine(FormatLine("Source tree", outputDirectory));
        writer.WriteLine(FormatLine("Last attempt", lastAttempt is null ? "never" : FormatElapsed(lastAttempt.Value, now)));
    }

    /// <summary>Formats one "label · value" status row with the column width every row shares.</summary>
    private static string FormatLine(string label, string value) =>
        $"{label,-18} ·  {TerminalTextSanitizer.Sanitize(value)}";

    private static string FormatElapsed(DateTimeOffset time, DateTimeOffset now)
    {
        var elapsed = now - time;
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
