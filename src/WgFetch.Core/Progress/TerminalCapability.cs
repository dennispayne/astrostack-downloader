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

    public bool InputRedirected { get; init; }

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
            InputRedirected = Console.IsInputRedirected,
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

    /// <summary>
    /// True only when a real, attached terminal can accept prompts. Unlike <see cref="Detect"/> — which
    /// also degrades rendering for cosmetic reasons such as <c>--plain</c>, <c>--no-color</c>, or
    /// <c>NO_COLOR</c> — a genuinely attached TTY still accepts interactive input under those flags, so
    /// they must not be treated as "no terminal" here. Only the absence of a real terminal (redirected
    /// streams, CI, <c>--json</c>, <c>TERM=dumb</c>, or no <c>TERM</c> on a non-Windows host) disqualifies it.
    /// </summary>
    public static bool IsInteractiveTerminal(TerminalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.OutputRedirected ||
            environment.ErrorRedirected ||
            environment.InputRedirected ||
            environment.CiSet ||
            environment.JsonRequested ||
            string.Equals(environment.Term, "dumb", StringComparison.OrdinalIgnoreCase) ||
            (!environment.IsWindows && string.IsNullOrEmpty(environment.Term)))
        {
            return false;
        }

        return true;
    }
}
