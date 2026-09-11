using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WgFetch.Core.Logging;

/// <summary>
/// A minimal, AOT-friendly logger that writes redacted, structured lines to a text writer — stderr by
/// default, so a <c>--json</c> stdout stays parseable (docs/REQUIREMENTS.md, "Observability").
/// </summary>
public sealed class RedactingConsoleLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _writer;
    private readonly LogLevel _minimum;
    private readonly IReadOnlyList<string> _secrets;
    private readonly object _gate = new();

    public RedactingConsoleLoggerProvider(TextWriter? writer, LogLevel minimum, IEnumerable<string>? secrets = null)
    {
        _writer = writer ?? Console.Error;
        _minimum = minimum;
        _secrets = secrets?.ToArray() ?? Array.Empty<string>();
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() => _writer.Flush();

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {Abbreviate(level)} {category}: {SecretRedactor.Redact(message, _secrets)}";
        if (exception is not null)
        {
            line += $" | {SecretRedactor.Redact(exception.Message, _secrets)}";
        }

        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    private sealed class Logger(RedactingConsoleLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

/// <summary>Maps the <c>--log-level</c> flag onto <see cref="LogLevel"/>.</summary>
public static class LogLevelParser
{
    public static LogLevel Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" or "information" or null or "" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "none" or "off" => LogLevel.None,
        _ => LogLevel.Information,
    };

    public static bool IsValid(string? value) => value?.ToLowerInvariant() is
        null or "" or "trace" or "debug" or "info" or "information" or "warn" or "warning" or "error" or "none" or "off";
}

/// <summary>
/// Timing instrumentation for every phase — search, page fetch, HTML reduction, inference,
/// verification, download, hash — logged at debug and summarised at end of run.
/// </summary>
public sealed class PhaseTimings
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TimeSpan> _totals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public IDisposable Measure(string phase, ILogger? logger = null) => new Scope(this, phase, logger);

    public void Record(string phase, TimeSpan elapsed)
    {
        lock (_gate)
        {
            _totals[phase] = _totals.TryGetValue(phase, out var total) ? total + elapsed : elapsed;
            _counts[phase] = _counts.TryGetValue(phase, out var count) ? count + 1 : 1;
        }
    }

    public IReadOnlyDictionary<string, TimeSpan> Totals
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, TimeSpan>(_totals, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Deterministically ordered end-of-run summary.</summary>
    public string Summarize()
    {
        lock (_gate)
        {
            if (_totals.Count == 0)
            {
                return "No phases recorded.";
            }

            var lines = _totals
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}: {pair.Value.TotalMilliseconds:F0}ms across {_counts[pair.Key]} call(s)");
            return "Timings — " + string.Join("; ", lines);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly PhaseTimings _owner;
        private readonly string _phase;
        private readonly ILogger? _logger;
        private readonly long _start = Stopwatch.GetTimestamp();

        public Scope(PhaseTimings owner, string phase, ILogger? logger)
        {
            _owner = owner;
            _phase = phase;
            _logger = logger;
        }

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_start);
            _owner.Record(_phase, elapsed);
            _logger?.LogDebug("Phase {Phase} took {Elapsed:F0}ms.", _phase, elapsed.TotalMilliseconds);
        }
    }
}
