namespace WgFetch.Core.Progress;

/// <summary>
/// Astrophotography phase vocabulary (docs/REQUIREMENTS.md, "Progress display"). Theming never comes
/// at the cost of clarity.
/// </summary>
public enum AcquisitionPhase
{
    /// <summary>Prereqs and refresh.</summary>
    Calibrating,

    /// <summary>Discovery — resolving a name to a candidate installer URL.</summary>
    Acquiring,

    /// <summary>Downloading.</summary>
    Tracking,

    /// <summary>Verification — the mechanical gate and hashing.</summary>
    PlateSolving,

    /// <summary>Writing manifests, index and provenance.</summary>
    Stacking,

    Done,
    Skipped,
    Failed,
}

public static class PhaseVocabulary
{
    public static string Describe(AcquisitionPhase phase) => phase switch
    {
        AcquisitionPhase.Calibrating => "calibrating",
        AcquisitionPhase.Acquiring => "acquiring",
        AcquisitionPhase.Tracking => "tracking",
        AcquisitionPhase.PlateSolving => "plate solving",
        AcquisitionPhase.Stacking => "stacking",
        AcquisitionPhase.Done => "acquired",
        AcquisitionPhase.Skipped => "skipped",
        AcquisitionPhase.Failed => "failed",
        _ => phase.ToString().ToLowerInvariant(),
    };

    /// <summary>Moon-phase spinner frames, with an ASCII fallback for terminals without wide glyphs.</summary>
    public static IReadOnlyList<string> MoonFrames { get; } = ["🌑", "🌒", "🌓", "🌔", "🌕", "🌖", "🌗", "🌘"];

    public static IReadOnlyList<string> AsciiFrames { get; } = ["-", "\\", "|", "/"];
}

/// <summary>A short "session report" summarising a completed run.</summary>
public sealed record SessionReport
{
    public int TargetsAcquired { get; init; }

    public int FramesRejected { get; init; }

    public int TargetsSkipped { get; init; }

    public TimeSpan IntegrationTime { get; init; }

    public long BytesAcquired { get; init; }

    public string Render() =>
        $"Session report: {TargetsAcquired} target(s) acquired, {TargetsSkipped} skipped, " +
        $"{FramesRejected} frame(s) rejected, integration time {IntegrationTime.TotalSeconds:F1}s, " +
        $"{BytesAcquired / 1024.0 / 1024.0:F1} MiB.";
}

/// <summary>
/// Progress sink. Warnings and errors are never hidden inside animation and always survive plain mode.
/// </summary>
public interface IProgressRenderer : IAsyncDisposable
{
    public void StartTarget(string target);

    public void Update(string target, AcquisitionPhase phase, string? detail = null, double? fraction = null);

    public void Complete(string target, AcquisitionPhase phase, string? detail = null);

    public void Warn(string message);

    public void Error(string message);

    public void Report(SessionReport report);
}

/// <summary>
/// Plain incremental output containing zero ANSI escape sequences. Human-readable output goes to the
/// error stream so a <c>--json</c> stdout stays parseable.
/// </summary>
public sealed class PlainProgressRenderer : IProgressRenderer
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();
    private readonly Dictionary<string, AcquisitionPhase> _phases = new(StringComparer.OrdinalIgnoreCase);

    public PlainProgressRenderer(TextWriter? writer = null) => _writer = writer ?? Console.Error;

    public void StartTarget(string target)
    {
        lock (_gate)
        {
            _phases[target] = AcquisitionPhase.Acquiring;
            _writer.WriteLine($"[{target}] acquiring");
        }
    }

    public void Update(string target, AcquisitionPhase phase, string? detail = null, double? fraction = null)
    {
        lock (_gate)
        {
            // Only emit a line when the phase actually changes, so a pipe does not fill with noise.
            if (_phases.TryGetValue(target, out var previous) && previous == phase && detail is null)
            {
                return;
            }

            _phases[target] = phase;
            var suffix = detail is null ? string.Empty : $" — {detail}";
            _writer.WriteLine($"[{target}] {PhaseVocabulary.Describe(phase)}{suffix}");
        }
    }

    public void Complete(string target, AcquisitionPhase phase, string? detail = null)
    {
        lock (_gate)
        {
            _phases[target] = phase;
            var suffix = detail is null ? string.Empty : $" — {detail}";
            _writer.WriteLine($"[{target}] {PhaseVocabulary.Describe(phase)}{suffix}");
        }
    }

    public void Warn(string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"warning: {message}");
        }
    }

    public void Error(string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"error: {message}");
        }
    }

    public void Report(SessionReport report)
    {
        lock (_gate)
        {
            _writer.WriteLine(report.Render());
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer.Flush();
        return ValueTask.CompletedTask;
    }
}
