using WgFetch.Core.Cli;

namespace WgFetch.Cli;

/// <summary>
/// Thin shell around <see cref="CommandRunner"/>. Everything testable lives in WgFetch.Core so the
/// NativeAOT entry point stays a few lines (docs/REQUIREMENTS.md, "Testing").
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C cancels cooperatively so partial files and metadata are cleaned up.
            e.Cancel = !cancellation.IsCancellationRequested;
            cancellation.Cancel();
        };

        var runner = new CommandRunner(Console.Out, Console.Error);
        return (int)await runner.RunAsync(args, cancellation.Token).ConfigureAwait(false);
    }
}
