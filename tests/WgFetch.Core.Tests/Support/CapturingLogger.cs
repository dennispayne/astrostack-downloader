using Microsoft.Extensions.Logging;

namespace WgFetch.Core.Tests.Support;

/// <summary>
/// An <see cref="ILogger"/> that records what was written, so tests can assert on the reasons the
/// verification gate gives for accepting or rejecting a candidate (docs/REQUIREMENTS.md: "every
/// rejection is logged with its reason") and that no secret ever reaches a log sink.
/// </summary>
public sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
