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

    /// <summary>
    /// Lowercase block-pixel glyphs for "wgfetch", six rows tall so ascenders (f, t, h) and the
    /// descender (g) read correctly; every other letter leaves the unused rows blank. Each letter is
    /// 5 columns wide and letters are joined with a single blank column, matching ordinary
    /// letter-spacing in a monospace font.
    /// </summary>
    private static readonly string[] LogoRows = BuildLogoRows();

    private static readonly string[] GradientPalette = ["#67e8f9", "#60a5fa", "#818cf8", "#8b5cf6", "#a78bfa"];

    internal static IRenderable CreateInteractiveHeader(int? terminalWidth = null)
    {
        if (terminalWidth is > 0 and < 45)
        {
            return new Markup(
                "[#67e8f9]wgfetch[/]\n" +
                "[#60a5fa]resolve[/]  [#818cf8]•[/]  [#8b5cf6]verify[/]  [#a78bfa]•[/]  [#67e8f9]download[/]");
        }

        var logo = string.Join(
            '\n',
            LogoRows.Select((row, index) => $"[{GradientPalette[index % GradientPalette.Length]}]{row}[/]"));

        return new Markup(
            logo + "\n\n" +
            "[#67e8f9]resolve[/]  [#60a5fa]•[/]  [#818cf8]verify[/]  [#8b5cf6]•[/]  [#a78bfa]download[/]");
    }

    private static string[] BuildLogoRows()
    {
        // Each letter is a 5-column-wide, 6-row-tall glyph (row 0 is ascender space, rows 1-4 are the
        // x-height body, row 5 is descender space), so the whole word lines up on a shared baseline.
        string[] w = ["     ", "█   █", "█ █ █", "██ ██", "█   █", "     "];
        string[] g = ["     ", " ███ ", "█   █", " ████", "    █", " ███ "];
        string[] f = ["  ██ ", " █   ", "████ ", " █   ", " █   ", "     "];
        string[] e = ["     ", " ███ ", "█████", "█    ", " ███ ", "     "];
        string[] t = [" █   ", "████ ", " █   ", " █   ", "  ██ ", "     "];
        string[] c = ["     ", " ███ ", "█    ", "█    ", " ███ ", "     "];
        string[] h = ["█    ", "█    ", "████ ", "█   █", "█   █", "     "];

        var letters = new[] { w, g, f, e, t, c, h };
        return Enumerable.Range(0, 6)
            .Select(row => string.Join(" ", letters.Select(letter => letter[row])))
            .ToArray();
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
