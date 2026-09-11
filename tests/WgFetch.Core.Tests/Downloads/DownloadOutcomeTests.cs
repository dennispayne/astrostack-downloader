using System.Security.Cryptography;
using WgFetch.Core.Cli;
using WgFetch.Core.Downloads;
using WgFetch.Core.Tests.Support;
using WgFetch.Core.Verification;

namespace WgFetch.Core.Tests.Downloads;

/// <summary>
/// The reasons and exit codes a download reports are part of the CLI contract and of the JSON event
/// stream (docs/REQUIREMENTS.md, "Exit codes" and "Downloads — resumable and atomic"), so they are
/// asserted exactly rather than by status alone.
/// </summary>
public sealed class DownloadOutcomeTests
{
    private const string Url = "https://nighttime-imaging.eu/download/NINASetup.exe";

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Theory]
    [InlineData(DownloadStatus.Downloaded, ExitCode.Success, true)]
    [InlineData(DownloadStatus.AlreadyPresent, ExitCode.Success, true)]
    [InlineData(DownloadStatus.Resumed, ExitCode.Success, true)]
    [InlineData(DownloadStatus.HashMismatch, ExitCode.HashMismatch, false)]
    [InlineData(DownloadStatus.RequiresAuthentication, ExitCode.RequiresAuth, false)]
    [InlineData(DownloadStatus.NetworkError, ExitCode.NetworkError, false)]
    [InlineData(DownloadStatus.Cancelled, ExitCode.Cancelled, false)]
    [InlineData(DownloadStatus.LengthMismatch, ExitCode.VerificationFailed, false)]
    [InlineData(DownloadStatus.VerificationFailed, ExitCode.VerificationFailed, false)]
    public void Every_status_maps_to_one_exit_code_and_one_notion_of_success(
        DownloadStatus status,
        ExitCode exitCode,
        bool success)
    {
        var result = new DownloadResult { Status = status, Reason = "test" };

        Assert.Equal(exitCode, result.ToExitCode());
        Assert.Equal(success, result.Success);
        Assert.Equal(InstallerFormat.Unknown, result.Format);
    }

    [Fact]
    public async Task A_completed_download_reports_that_it_was_downloaded_and_verified()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var logger = new CapturingLogger();
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(payload));
        var final = temp.Combine("installers", "NINASetup.exe");

        var result = await new InstallerDownloader(http, logger).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal("downloaded and verified", result.Reason);
        Assert.False(result.Resumed);
        Assert.Equal(payload.Length, result.BytesWritten);
        Assert.Equal(InstallerFormat.PortableExecutable, result.Format);
        Assert.Null(result.UpstreamHashMatched);
        Assert.Contains(logger.Messages, m => m.Contains("Acquired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_already_present_file_reports_the_matching_hash_and_contacts_nothing()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.Zip();
        var final = temp.Combine("installers", "bundle.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await File.WriteAllBytesAsync(final, payload, CancellationToken.None);
        var logger = new CapturingLogger();

        var result = await new InstallerDownloader(new StubHttpGateway(), logger).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final, UpstreamSha256 = Sha256Of(payload) },
            CancellationToken.None);

        Assert.Equal("file already present with matching SHA256", result.Reason);
        Assert.True(result.UpstreamHashMatched);
        Assert.Equal(payload.Length, result.BytesWritten);
        Assert.Equal(final, result.Path);
        Assert.Contains(logger.Messages, m => m.Contains("already matches the expected hash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_present_file_with_a_different_hash_is_re_fetched_rather_than_trusted()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var final = temp.Combine("installers", "NINASetup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await File.WriteAllBytesAsync(final, FakeInstaller.Zip(), CancellationToken.None);
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(payload));

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final, UpstreamSha256 = Sha256Of(payload) },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.NotEmpty(http.Requests);
        Assert.Equal(payload, await File.ReadAllBytesAsync(final, CancellationToken.None));
    }

    [Fact]
    public async Task A_length_mismatch_names_both_lengths()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(payload));
        var final = temp.Combine("installers", "NINASetup.exe");

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                ExpectedLength = payload.Length + 1,
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.LengthMismatch, result.Status);
        Assert.Equal($"downloaded {payload.Length} bytes but expected {payload.Length + 1}", result.Reason);
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task A_fatal_hash_mismatch_names_both_hashes_and_logs_the_redacted_url()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var wrong = Sha256Of(FakeInstaller.Zip());
        var logger = new CapturingLogger();
        var http = new StubHttpGateway().Map(Url + "?token=secret", StubResponse.Binary(payload));
        var final = temp.Combine("installers", "NINASetup.exe");

        var result = await new InstallerDownloader(http, logger).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url + "?token=secret"),
                FinalPath = final,
                UpstreamSha256 = wrong,
                RequireHashMatch = true,
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.HashMismatch, result.Status);
        Assert.Equal($"computed SHA256 {Sha256Of(payload)} does not match upstream {wrong}", result.Reason);
        Assert.False(result.UpstreamHashMatched);
        Assert.False(File.Exists(final));
        Assert.Contains(logger.Messages, m => m.Contains("SHA256 mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("token=secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resumed_download_says_it_resumed()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var installers = temp.Combine("installers");
        var partialDir = Path.Combine(installers, InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);

        var partial = Path.Combine(partialDir, "NINASetup-abc.tmp");
        await File.WriteAllBytesAsync(partial, payload[..1024], CancellationToken.None);
        await File.WriteAllTextAsync(
            partial + ".meta",
            $$"""
              {"url":"{{Url}}","validator":"\"v1\"","expectedLength":{{payload.Length}},"bytesWritten":1024,"upstreamSha256":null}
              """,
            CancellationToken.None);

        var http = new StubHttpGateway().Map(Url, _ => StubResponse.Binary(payload) with
        {
            StatusCode = 206,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/octet-stream",
                ["ETag"] = "\"v1\"",
                ["Accept-Ranges"] = "bytes",
            },
        });

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = temp.Combine("installers", "NINASetup.exe"),
                Validator = "\"v1\"",
                ServerAcceptsRanges = true,
                ExpectedLength = payload.Length,
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Resumed, result.Status);
        Assert.Equal("resumed and verified", result.Reason);
        Assert.True(result.Resumed);
    }

    [Fact]
    public void Stale_partials_are_pruned_only_once_they_are_older_than_the_limit()
    {
        using var temp = new TempDirectory();
        var installers = temp.Combine("installers");
        var partialDir = Path.Combine(installers, InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);

        var now = DateTimeOffset.UtcNow;
        var exactlyAtLimit = Path.Combine(partialDir, "fresh.part");
        var older = Path.Combine(partialDir, "stale.part");
        File.WriteAllText(exactlyAtLimit, "a");
        File.WriteAllText(older, "b");
        File.SetLastWriteTimeUtc(exactlyAtLimit, now.UtcDateTime - TimeSpan.FromHours(24));
        File.SetLastWriteTimeUtc(older, now.UtcDateTime - TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));

        var logger = new CapturingLogger();
        var removed = new InstallerDownloader(new StubHttpGateway(), logger)
            .CleanStalePartials(installers, TimeSpan.FromHours(24), now);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(exactlyAtLimit));
        Assert.False(File.Exists(older));
        Assert.Contains(logger.Messages, m => m.Contains("Removed stale partial", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleaning_a_directory_without_partials_is_a_no_op()
    {
        using var temp = new TempDirectory();

        var removed = new InstallerDownloader(new StubHttpGateway())
            .CleanStalePartials(temp.Combine("installers"), TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task A_request_without_a_directory_component_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new InstallerDownloader(new StubHttpGateway()).DownloadAsync(null!, CancellationToken.None));
    }
}
