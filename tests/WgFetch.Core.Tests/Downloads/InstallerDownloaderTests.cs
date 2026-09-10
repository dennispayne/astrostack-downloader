using System.Security.Cryptography;
using System.Text;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Downloads;
using WgFetch.Core.Tests.Support;

namespace WgFetch.Core.Tests.Downloads;

/// <summary>
/// Resume must never splice two builds together and the final path must never exist unverified
/// (docs/REQUIREMENTS.md, "Downloads — resumable and atomic").
/// </summary>
public sealed class InstallerDownloaderTests
{
    private const string Url = "https://nighttime-imaging.eu/download/NINASetup.exe";

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Downloads_verifies_and_atomically_publishes()
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
                ExpectedLength = payload.Length,
                UpstreamSha256 = Sha256Of(payload),
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(Sha256Of(payload), result.Sha256);
        Assert.True(result.UpstreamHashMatched);
        Assert.Equal(payload, await File.ReadAllBytesAsync(final, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName)));
    }

    [Fact]
    public async Task Skips_the_download_when_the_final_file_already_matches_the_expected_hash()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.Zip();
        var final = temp.Combine("installers", "bundle.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await File.WriteAllBytesAsync(final, payload, CancellationToken.None);

        // Deny-all gateway: any network call at all fails the test.
        var http = new StubHttpGateway();

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final, UpstreamSha256 = Sha256Of(payload) },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.AlreadyPresent, result.Status);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Rejects_a_login_page_without_writing_the_final_path()
    {
        using var temp = new TempDirectory();
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>login</body></html>" + new string('x', 100_000));
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(html));
        var final = temp.Combine("installers", "NINASetup.exe");

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.VerificationFailed, result.Status);
        Assert.False(File.Exists(final));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName)));
    }

    [Fact]
    public async Task Rejects_an_unknown_payload_format_without_writing_the_final_path()
    {
        using var temp = new TempDirectory();
        var junk = new byte[80_000];
        Array.Fill(junk, (byte)0x5A);
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(junk));
        var final = temp.Combine("installers", "NINASetup.exe");

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.VerificationFailed, result.Status);
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task A_truncated_response_whose_length_matches_still_fails_hash_verification()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var truncated = payload[..(payload.Length - 1024)];
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(truncated) with
        {
            AdvertisedLength = truncated.Length,
        });
        var final = temp.Combine("installers", "NINASetup.exe");

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                ExpectedLength = truncated.Length,
                UpstreamSha256 = Sha256Of(payload),
                RequireHashMatch = true,
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.HashMismatch, result.Status);
        Assert.Equal(ExitCode.HashMismatch, result.ToExitCode());
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task A_short_response_fails_the_length_check()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(payload[..(payload.Length / 2)]));
        var final = temp.Combine("installers", "NINASetup.exe");

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final, ExpectedLength = payload.Length },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.LengthMismatch, result.Status);
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task Hash_mismatch_is_reported_but_not_fatal_without_require_hash_match()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.Msi();
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(payload));
        var final = temp.Combine("installers", "setup.msi");

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                UpstreamSha256 = new string('a', 64),
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.False(result.UpstreamHashMatched);
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task Requires_authentication_is_surfaced_and_writes_nothing()
    {
        using var temp = new TempDirectory();
        var http = new StubHttpGateway().Map(Url, StubResponse.Status(403));
        var final = temp.Combine("installers", "pro.exe");

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest { Url = new Uri(Url), FinalPath = final },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.RequiresAuthentication, result.Status);
        Assert.Equal(ExitCode.RequiresAuth, result.ToExitCode());
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task Resumes_from_a_partial_when_the_server_honours_the_range_and_the_validator()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var final = temp.Combine("installers", "NINASetup.exe");
        var partialDir = Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);

        var half = payload.Length / 2;
        var partial = Path.Combine(partialDir, "NINASetup-abc.tmp");
        await File.WriteAllBytesAsync(partial, payload[..half], CancellationToken.None);
        await File.WriteAllTextAsync(
            partial + ".meta",
            $$"""
              {"url":"{{Url}}","validator":"\"v1\"","expectedLength":{{payload.Length}},"bytesWritten":{{half}},"upstreamSha256":null}
              """,
            CancellationToken.None);

        var http = new StubHttpGateway().Map(Url, request =>
        {
            Assert.Equal(half, request.RangeFrom);
            Assert.Equal("\"v1\"", request.IfRange);
            return StubResponse.Binary(payload) with { StatusCode = 206 };
        });

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                ExpectedLength = payload.Length,
                Validator = "\"v1\"",
                ServerAcceptsRanges = true,
                UpstreamSha256 = Sha256Of(payload),
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Resumed, result.Status);
        Assert.True(result.Resumed);
        Assert.Equal(payload, await File.ReadAllBytesAsync(final, CancellationToken.None));
    }

    [Fact]
    public async Task Restarts_instead_of_splicing_when_the_server_ignores_the_range_header()
    {
        using var temp = new TempDirectory();
        var oldBuild = FakeInstaller.PortableExecutable();
        var newBuild = FakeInstaller.Msi();
        var final = temp.Combine("installers", "NINASetup.exe");
        var partialDir = Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);

        var half = oldBuild.Length / 2;
        var partial = Path.Combine(partialDir, "NINASetup-abc.tmp");
        await File.WriteAllBytesAsync(partial, oldBuild[..half], CancellationToken.None);
        await File.WriteAllTextAsync(
            partial + ".meta",
            $$"""
              {"url":"{{Url}}","validator":"\"v1\"","expectedLength":{{newBuild.Length}},"bytesWritten":{{half}},"upstreamSha256":null}
              """,
            CancellationToken.None);

        // Server answers 200 with the full (different) build: the partial must be discarded, not spliced.
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(newBuild));

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                ExpectedLength = newBuild.Length,
                Validator = "\"v1\"",
                ServerAcceptsRanges = true,
                UpstreamSha256 = Sha256Of(newBuild),
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.False(result.Resumed);
        Assert.Equal(newBuild, await File.ReadAllBytesAsync(final, CancellationToken.None));
        Assert.Equal(Sha256Of(newBuild), result.Sha256);
    }

    [Fact]
    public async Task Does_not_resume_when_the_validator_changed()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.Zip();
        var final = temp.Combine("installers", "bundle.zip");
        var partialDir = Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);

        var partial = Path.Combine(partialDir, "bundle-abc.tmp");
        await File.WriteAllBytesAsync(partial, payload[..1000], CancellationToken.None);
        await File.WriteAllTextAsync(
            partial + ".meta",
            $$"""
              {"url":"{{Url}}","validator":"\"old\"","expectedLength":{{payload.Length}},"bytesWritten":1000,"upstreamSha256":null}
              """,
            CancellationToken.None);

        var requestedRanges = new List<long?>();
        var http = new StubHttpGateway().Map(Url, request =>
        {
            requestedRanges.Add(request.RangeFrom);
            return StubResponse.Binary(payload);
        });

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                ExpectedLength = payload.Length,
                Validator = "\"new\"",
                ServerAcceptsRanges = true,
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.All(requestedRanges, range => Assert.Null(range));
        Assert.Equal(Sha256Of(payload), result.Sha256);
    }

    [Fact]
    public async Task Never_resumes_without_a_validator()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.Zip();
        var final = temp.Combine("installers", "bundle.zip");
        var partialDir = Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);
        var partial = Path.Combine(partialDir, "bundle-abc.tmp");
        await File.WriteAllBytesAsync(partial, payload[..1000], CancellationToken.None);
        await File.WriteAllTextAsync(
            partial + ".meta",
            $$"""
              {"url":"{{Url}}","validator":null,"expectedLength":{{payload.Length}},"bytesWritten":1000,"upstreamSha256":null}
              """,
            CancellationToken.None);

        var http = new StubHttpGateway().Map(Url, request =>
        {
            Assert.Null(request.RangeFrom);
            return StubResponse.Binary(payload);
        });

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                ExpectedLength = payload.Length,
                Validator = null,
                ServerAcceptsRanges = true,
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
    }

    [Fact]
    public async Task No_resume_flag_forces_a_clean_refetch()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.Zip();
        var final = temp.Combine("installers", "bundle.zip");
        var partialDir = Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);
        var partial = Path.Combine(partialDir, "bundle-abc.tmp");
        await File.WriteAllBytesAsync(partial, payload[..1000], CancellationToken.None);
        await File.WriteAllTextAsync(
            partial + ".meta",
            $$"""
              {"url":"{{Url}}","validator":"\"v1\"","expectedLength":{{payload.Length}},"bytesWritten":1000,"upstreamSha256":null}
              """,
            CancellationToken.None);

        var http = new StubHttpGateway().Map(Url, request =>
        {
            Assert.Null(request.RangeFrom);
            return StubResponse.Binary(payload);
        });

        var result = await new InstallerDownloader(http).DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri(Url),
                FinalPath = final,
                ExpectedLength = payload.Length,
                Validator = "\"v1\"",
                ServerAcceptsRanges = true,
                NoResume = true,
            },
            CancellationToken.None);

        Assert.Equal(DownloadStatus.Downloaded, result.Status);
    }

    [Fact]
    public async Task Cancellation_cleans_up_partials_and_leaves_no_final_file()
    {
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(FakeInstaller.Zip()));
        var final = temp.Combine("installers", "bundle.zip");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InstallerDownloader(http).DownloadAsync(
                new DownloadRequest { Url = new Uri(Url), FinalPath = final },
                cts.Token));

        Assert.False(File.Exists(final));
    }

    [Fact]
    public void Cleans_stale_partials_by_age()
    {
        using var temp = new TempDirectory();
        var installers = temp.Combine("installers");
        var partialDir = Path.Combine(installers, InstallerDownloader.PartialDirectoryName);
        Directory.CreateDirectory(partialDir);

        var stale = Path.Combine(partialDir, "stale.tmp");
        var fresh = Path.Combine(partialDir, "fresh.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));

        var removed = new InstallerDownloader(new StubHttpGateway())
            .CleanStalePartials(installers, TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Concurrent_downloads_of_the_same_package_use_distinct_partial_files()
    {
        using var temp = new TempDirectory();
        var payload = FakeInstaller.PortableExecutable();
        var http = new StubHttpGateway().Map(Url, StubResponse.Binary(payload));
        var final = temp.Combine("installers", "NINASetup.exe");
        var downloader = new InstallerDownloader(http);

        var request = new DownloadRequest
        {
            Url = new Uri(Url),
            FinalPath = final,
            ExpectedLength = payload.Length,
            UpstreamSha256 = Sha256Of(payload),
        };

        var results = await Task.WhenAll(
            downloader.DownloadAsync(request, CancellationToken.None),
            downloader.DownloadAsync(request, CancellationToken.None));

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(payload, await File.ReadAllBytesAsync(final, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Combine("installers"), InstallerDownloader.PartialDirectoryName)));
    }
}
