using Microsoft.Extensions.Logging;
using WgFetch.Core.Tests.Support;
using WgFetch.Core.Verification;

namespace WgFetch.Core.Tests.Verification;

/// <summary>
/// Content-type handling in the gate. A recognised binary type passes silently; an unrecognised one is
/// not fatal but must be recorded before the decision defers to magic bytes; a markup type is fatal
/// (docs/REQUIREMENTS.md, verification gate checks 4 and 5).
/// </summary>
public sealed class VerificationGateContentTypeTests
{
    private const string Url = "https://nighttime-imaging.eu/download/NINASetup.exe";

    private static readonly DomainAllowlist Allowlist = new(["nighttime-imaging.eu"]);

    public static TheoryData<string> RecognisedBinaryContentTypes =>
    [
        "application/octet-stream",
        "application/x-msdownload",
        "application/x-msdos-program",
        "application/x-msi",
        "application/x-ms-installer",
        "application/x-msinstaller",
        "application/vnd.microsoft.portable-executable",
        "application/exe",
        "application/x-exe",
        "application/zip",
        "application/x-zip-compressed",
        "application/x-7z-compressed",
        "application/x-compressed",
        "application/x-download",
        "binary/octet-stream",
        "application/x-binary",
        "application/vnd.ms-cab-compressed",
        "application/msix",
        "application/vnd.ms-appx",
    ];

    [Theory]
    [MemberData(nameof(RecognisedBinaryContentTypes))]
    public async Task Recognised_binary_content_types_are_accepted_without_a_deferral_note(string contentType)
    {
        var (result, logger) = await VerifyWithContentTypeAsync(contentType);

        Assert.True(result.Accepted);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("unrecognised", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("APPLICATION/ZIP")]
    [InlineData("  application/zip  ")]
    [InlineData("application/zip; charset=binary")]
    public async Task Content_type_matching_ignores_case_whitespace_and_parameters(string contentType)
    {
        var (result, logger) = await VerifyWithContentTypeAsync(contentType);

        Assert.True(result.Accepted);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("unrecognised", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/x-not-a-real-type")]
    public async Task An_unrecognised_content_type_defers_to_magic_bytes_and_says_so(string contentType)
    {
        var (result, logger) = await VerifyWithContentTypeAsync(contentType);

        Assert.True(result.Accepted);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("unrecognised", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_default_options_and_logger_are_used_when_none_are_supplied()
    {
        // Constructed with neither options nor logger: the default minimum installer size must still
        // apply, and rejection logging must not fault on a null logger.
        var http = new StubHttpGateway().Map(Url, _ => StubResponse.Binary(FakeInstaller.PortableExecutable(2048)));
        var gate = new VerificationGate(http);

        var result = await gate.VerifyAsync(new Uri(Url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.ImplausibleLength, result.Status);
        Assert.Equal(2048, result.ContentLength);
    }

    [Fact]
    public async Task A_rejection_is_logged_with_its_reason_and_keeps_the_redirect_chain()
    {
        const string hop = "https://nighttime-imaging.eu/cdn/NINASetup.exe";
        var logger = new CapturingLogger();
        var http = new StubHttpGateway()
            .Map(Url, _ => StubResponse.Redirect(hop))
            .Map(hop, _ => StubResponse.Html("<html><body>login</body></html>"));

        var result = await new VerificationGate(http, new VerificationOptions(), logger)
            .VerifyAsync(new Uri(Url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.NotBinaryContentType, result.Status);
        Assert.Equal(new[] { new Uri(hop) }, result.RedirectChain);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("Verification rejected", StringComparison.Ordinal));
    }

    private static async Task<(VerificationResult Result, CapturingLogger Logger)> VerifyWithContentTypeAsync(
        string contentType)
    {
        var logger = new CapturingLogger();
        var http = new StubHttpGateway().Map(
            Url,
            _ => StubResponse.Binary(FakeInstaller.PortableExecutable(), contentType));

        var result = await new VerificationGate(http, new VerificationOptions(), logger)
            .VerifyAsync(new Uri(Url), Allowlist, CancellationToken.None);

        return (result, logger);
    }
}
