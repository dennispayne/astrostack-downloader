using Spectre.Console;
using Spectre.Console.Rendering;

namespace WgFetch.Core.Progress;

/// <summary>
/// Themed live rendering for interactive ANSI terminals: one row per target, a moon-phase spinner,
/// and a capped refresh rate so theming costs negligible CPU. Warnings and errors are rendered as
/// their own rows so animation can never hide them (docs/REQUIREMENTS.md, "Progress display").
/// </summary>
public sealed class LiveProgressRenderer : IProgressRenderer
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(125);

    private readonly IAnsiConsole _console;
    private readonly bool _unicode;
    private readonly object _gate = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, RowState> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _messages = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private int _frame;
    private SessionReport? _report;

    public LiveProgressRenderer(IAnsiConsole? console = null, bool unicode = true)
    {
        _console = console ?? AnsiConsole.Console;
        _unicode = unicode;
        _loop = Task.Run(RunAsync);
    }

    private sealed record RowState(AcquisitionPhase Phase, string? Detail, double? Fraction);

    public void StartTarget(string target) => Set(target, AcquisitionPhase.Acquiring, null, null);

    public void Update(string target, AcquisitionPhase phase, string? detail = null, double? fraction = null) =>
        Set(target, phase, detail, fraction);

    public void Complete(string target, AcquisitionPhase phase, string? detail = null) =>
        Set(target, phase, detail, 1.0);

    public void Warn(string message)
    {
        lock (_gate)
        {
            _messages.Add($"[yellow]warning[/] {Markup.Escape(message)}");
        }
    }

    public void Error(string message)
    {
        lock (_gate)
        {
            _messages.Add($"[red]error[/] {Markup.Escape(message)}");
        }
    }

    public void Report(SessionReport report)
    {
        lock (_gate)
        {
            _report = report;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }

        // Always restore the terminal, on every exit path.
        _console.Cursor.Show();
        _console.Write(BuildRenderable());
        _cts.Dispose();
    }

    private void Set(string target, AcquisitionPhase phase, string? detail, double? fraction)
    {
        lock (_gate)
        {
            if (!_rows.ContainsKey(target))
            {
                _order.Add(target);
            }

            _rows[target] = new RowState(phase, detail, fraction);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await _console.Live(BuildRenderable())
                .StartAsync(async ctx =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _frame);
                        ctx.UpdateTarget(BuildRenderable());
                        ctx.Refresh();
                        await Task.Delay(RefreshInterval, _cts.Token).ConfigureAwait(false);
                    }
                })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private IRenderable BuildRenderable()
    {
        var table = new Table().Border(TableBorder.Minimal).HideHeaders();
        table.AddColumn(new TableColumn(string.Empty).NoWrap());
        table.AddColumn(new TableColumn(string.Empty));
        table.AddColumn(new TableColumn(string.Empty));

        lock (_gate)
        {
            var frames = _unicode ? PhaseVocabulary.MoonFrames : PhaseVocabulary.AsciiFrames;
            var spinner = frames[_frame % frames.Count];

            foreach (var target in _order)
            {
                var row = _rows[target];
                var glyph = row.Phase switch
                {
                    AcquisitionPhase.Done => _unicode ? "✔" : "+",
                    AcquisitionPhase.Failed => _unicode ? "✖" : "x",
                    AcquisitionPhase.Skipped => "-",
                    _ => spinner,
                };

                var detail = row.Detail is null ? string.Empty : Markup.Escape(row.Detail);
                if (row.Fraction is { } fraction && row.Phase == AcquisitionPhase.Tracking)
                {
                    detail = $"{fraction * 100:F0}% {detail}".Trim();
                }

                table.AddRow(
                    new Markup(glyph),
                    new Markup(Markup.Escape(target)),
                    new Markup($"{PhaseVocabulary.Describe(row.Phase)} {detail}".TrimEnd()));
            }

            foreach (var message in _messages)
            {
                table.AddRow(new Markup(" "), new Markup(" "), new Markup(message));
            }

            if (_report is { } report)
            {
                table.AddRow(new Markup(" "), new Markup(" "), new Markup(Markup.Escape(report.Render())));
            }
        }

        return table;
    }
}
