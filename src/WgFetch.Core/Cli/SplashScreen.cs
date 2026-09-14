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
        string? targetsError = null,
        int? terminalWidth = null)
    {
        return new Rows(
            CreateInteractiveHeader(terminalWidth),
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

    internal static IRenderable CreateInteractiveHeader(int? terminalWidth = null)
    {
        const string backslash = "\\";
        if (terminalWidth is > 0 and < 52)
        {
            return new Panel(new Markup(
                "[#67e8f9]█▄[/] [#60a5fa]WGFETCH[/] [#8b5cf6]▄█[/]\n" +
                "[#67e8f9]resolve[/] [#60a5fa]• verify[/] [#a78bfa]• download[/]"))
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.FromHex("#8b5cf6"))
                .Padding(1, 0);
        }

        var art = new Markup(
            "[#67e8f9]·[/]       [#60a5fa]✦[/]             [#8b5cf6]·[/]\n" +
            "              [#67e8f9]/" + backslash + "[/]\n" +
            "             [#60a5fa]/  " + backslash + "[/]       [#a78bfa]⋆[/]\n" +
            "        [#818cf8]o===/____" + backslash + "[/]\n" +
            "            [#8b5cf6](____)[/]\n\n" +
            "[#67e8f9]█   █  ███  █████ █████ █████  ███  █   █[/]\n" +
            "[#60a5fa]█   █ █     █     █       █   █     █   █[/]\n" +
            "[#818cf8]█ █ █ █ ███ ████  ████    █   █     █████[/]\n" +
            "[#8b5cf6]██ ██ █   █ █     █       █   █     █   █[/]\n" +
            "[#a78bfa]█   █  ███  █     █████   █    ███  █   █[/]\n\n" +
            "[#67e8f9]       resolve[/] [#60a5fa]•[/] [#818cf8]verify[/] [#8b5cf6]•[/] [#a78bfa]download[/]");

        return new Panel(art)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.FromHex("#8b5cf6"))
            .Padding(2, 1);
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
