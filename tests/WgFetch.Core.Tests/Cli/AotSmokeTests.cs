using System.Diagnostics;
using System.Text.Json;

namespace WgFetch.Core.Tests.Cli;

/// <summary>
/// Smoke tests against a published NativeAOT binary (docs/REQUIREMENTS.md, "Testing": "NativeAOT smoke
/// tests that run the published binary, not just the JIT build"). They are skipped unless
/// <c>WGFETCH_AOT_BINARY</c> points at one, so the default hermetic run stays fast.
/// </summary>
[Trait("Category", "Aot")]
public sealed class AotSmokeTests
{
    private static string? BinaryPath => Environment.GetEnvironmentVariable("WGFETCH_AOT_BINARY");

    /// <summary>
    /// These are opt-in: with no published binary to point at there is nothing to smoke-test, and the
    /// test framework in use has no first-class runtime skip, so the body is simply a no-op.
    /// </summary>
    private static bool HasBinary => !string.IsNullOrWhiteSpace(BinaryPath) && File.Exists(BinaryPath);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(params string[] args)
    {
        var binary = BinaryPath!;
        var info = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<int> RunAttachedToConsoleAsync(params string[] args)
    {
        var info = new ProcessStartInfo(BinaryPath!)
        {
            UseShellExecute = false,
        };
        info.Environment.Remove("CI");
        info.Environment.Remove("NO_COLOR");

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }

    [Fact]
    public async Task PublishedBinary_PrintsHelp()
    {
        if (!HasBinary)
        {
            return;
        }

        var (exit, stdout, _) = await RunAsync("--help");

        Assert.Equal(0, exit);
        Assert.Contains("fetch", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishedBinary_PrintsVersion()
    {
        if (!HasBinary)
        {
            return;
        }

        var (exit, stdout, _) = await RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Matches(@"^\d+\.\d+\.\d+", stdout.Trim());
    }

    [Fact]
    public async Task PublishedBinary_RejectsUnknownVerbWithTheUsageExitCode()
    {
        if (!HasBinary)
        {
            return;
        }

        var (exit, _, stderr) = await RunAsync("frobnicate");

        Assert.Equal((int)ExitCode.UsageError, exit);
        Assert.Contains("wgfetch", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishedBinary_ExecutesInteractiveLandingWhenAttachedToConsole()
    {
        if (!HasBinary || Console.IsOutputRedirected)
        {
            return;
        }

        var exit = await RunAttachedToConsoleAsync();

        Assert.Equal((int)ExitCode.UsageError, exit);
    }

    /// <summary>
    /// The trimmer is the risk here: reflection-based JSON or console rendering that survives the JIT
    /// build can vanish under AOT, so the published binary must still produce parseable events.
    /// </summary>
    [Fact]
    public async Task PublishedBinary_EmitsParseableJsonEvents()
    {
        if (!HasBinary)
        {
            return;
        }

        var repo = Path.Combine(Path.GetTempPath(), "wgfetch-aot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        try
        {
            var (exit, stdout, stderr) = await RunAsync("add", "nina", "--winget-repo", repo, "--json");

            Assert.True(exit == 0, $"exit={exit} stderr={stderr}");
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(lines);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.True(document.RootElement.TryGetProperty("event", out _), line);
            }

            Assert.True(File.Exists(Path.Combine(repo, "targets.yaml")));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task PublishedBinary_RendersTheStatusTableWithoutAnsiWhenRedirected()
    {
        if (!HasBinary)
        {
            return;
        }

        var repo = Path.Combine(Path.GetTempPath(), "wgfetch-aot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        try
        {
            await RunAsync("add", "nina", "--winget-repo", repo);
            var (exit, stdout, _) = await RunAsync("status", "--winget-repo", repo);

            Assert.Equal(0, exit);
            Assert.Contains("nina", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain('\u001b', stdout);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
