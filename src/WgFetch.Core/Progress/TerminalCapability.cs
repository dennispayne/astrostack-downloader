namespace WgFetch.Core.Progress;

/// <summary>How rich the output stream may be.</summary>
public enum TerminalMode
{
    /// <summary>Interactive ANSI terminal: live rows, colour, themed spinner.</summary>
    Interactive,

    /// <summary>Plain incremental lines. **Zero escape sequences**, ever.</summary>
    Plain,
}

/// <summary>Inputs to terminal capability detection, injectable so tests need no real console.</summary>
public sealed record TerminalEnvironment
{
    public bool OutputRedirected { get; init; }

    public bool ErrorRedirected { get; init; }

    public string? Term { get; init; }

    public bool NoColorSet { get; init; }

    public bool CiSet { get; init; }

    public bool PlainRequested { get; init; }

    public bool NoColorRequested { get; init; }

    public bool JsonRequested { get; init; }

    /// <summary>Windows consoles are ANSI-capable without setting <c>TERM</c>.</summary>
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    public static TerminalEnvironment FromProcess(bool plainRequested = false, bool noColorRequested = false, bool jsonRequested = false) =>
        new()
        {
            OutputRedirected = Console.IsOutputRedirected,
            ErrorRedirected = Console.IsErrorRedirected,
            Term = Environment.GetEnvironmentVariable("TERM"),
            NoColorSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
            CiSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
            PlainRequested = plainRequested,
            NoColorRequested = noColorRequested,
            JsonRequested = jsonRequested,
            IsWindows = OperatingSystem.IsWindows(),
        };
}

/// <summary>
/// Decides between themed live rendering and plain incremental output. Escape codes must never reach
/// a log file, a pipe, CI, <c>TERM=dumb</c>, or a run with <c>NO_COLOR</c>/<c>--no-color</c>/<c>--plain</c>
/// (docs/REQUIREMENTS.md, "Progress display").
/// </summary>
public static class TerminalCapability
{
    public static TerminalMode Detect(TerminalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.PlainRequested ||
            environment.NoColorRequested ||
            environment.NoColorSet ||
            environment.CiSet ||
            environment.JsonRequested ||
            environment.OutputRedirected ||
            environment.ErrorRedirected ||
            string.Equals(environment.Term, "dumb", StringComparison.OrdinalIgnoreCase) ||
            (!environment.IsWindows && string.IsNullOrEmpty(environment.Term)))
        {
            return TerminalMode.Plain;
        }

        return TerminalMode.Interactive;
    }
}
