using System.ComponentModel;
using System.Diagnostics;

namespace WgFetch.Core.Tests.Support;

/// <summary>
/// Creates the special files that fail-closed readers must reject. Creation is best effort: hosts
/// without <c>mkfifo</c> report failure so the caller can skip rather than fail on an unsupported host.
/// </summary>
internal static class SpecialFiles
{
    internal static async Task<bool> TryCreateFifoAsync(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        Process? mkfifo;
        try
        {
            mkfifo = Process.Start(new ProcessStartInfo
            {
                FileName = "mkfifo",
                ArgumentList = { path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
        }
        catch (Win32Exception)
        {
            return false;
        }

        if (mkfifo is null)
        {
            return false;
        }

        await mkfifo.WaitForExitAsync(CancellationToken.None);
        return mkfifo.ExitCode == 0;
    }
}
