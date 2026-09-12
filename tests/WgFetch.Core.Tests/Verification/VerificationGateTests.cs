using WgFetch.Core.Abstractions;
using WgFetch.Core.Tests.Support;
using WgFetch.Core.Verification;

namespace WgFetch.Core.Tests.Verification;

/// <summary>
/// The verification gate is the security boundary: no model output may move a URL past it
/// (docs/REQUIREMENTS.md, "The central safety invariant" and the adversarial cases in "Testing").
/// </summary>
public sealed class VerificationGateTests
{
    private static readonly DomainAllowlist Allowlist = new(["nighttime-imaging.eu", "github.com"]);

    private static VerificationGate Gate(StubHttpGateway http) => new(http, new VerificationOptions());

    [Fact]
    public async Task Redirect_follower_rejects_a_non_https_candidate_before_any_request()
    {
        var http = new StubHttpGateway();

        await using var result = await Gate(http).FollowAllowedRedirectsAsync(
            new HttpRequestSpec { Url = new Uri("http://github.com/x/setup.exe") },
            Allowlist,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.NotHttps, result.FailureStatus);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Redirect_follower_rejects_a_redirect_to_http()
    {
        const string url = "https://github.com/x/setup.exe";
        var http = new StubHttpGateway().Map(url, StubResponse.Redirect("http://github.com/x/setup.exe"));

        await using var result = await Gate(http).FollowAllowedRedirectsAsync(
            new HttpRequestSpec { Url = new Uri(url) },
            Allowlist,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.NotHttps, result.FailureStatus);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Redirect_follower_rejects_a_redirect_without_a_location()
    {
        const string url = "https://github.com/x/setup.exe";
        var http = new StubHttpGateway().Map(url, new StubResponse { StatusCode = 302 });

        await using var result = await Gate(http).FollowAllowedRedirectsAsync(
            new HttpRequestSpec { Url = new Uri(url) },
            Allowlist,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.RequestFailed, result.FailureStatus);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Redirect_follower_stops_at_the_configured_limit()
    {
        const string url = "https://github.com/loop";
        var http = new StubHttpGateway().Map(url, StubResponse.Redirect(url));
        var gate = new VerificationGate(http, new VerificationOptions { MaximumRedirects = 1 });

        await using var result = await gate.FollowAllowedRedirectsAsync(
            new HttpRequestSpec { Url = new Uri(url) },
            Allowlist,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.TooManyRedirects, result.FailureStatus);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(new Uri(url), result.FinalUrl);
        Assert.Single(result.RedirectChain);
    }

    [Fact]
    public async Task Redirect_follower_strips_credential_headers_when_the_authority_changes()
    {
        const string source = "https://github.com/x/setup.exe";
        const string target = "https://objects.github.com/asset/setup.exe";
        var http = new StubHttpGateway()
            .Map(source, StubResponse.Redirect(target))
            .Map(target, StubResponse.Binary(FakeInstaller.PortableExecutable()));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/octet-stream",
            ["Authorization"] = "******",
            ["Cookie"] = "session=credential-value",
            ["X-Api-Key"] = "credential-value",
            ["Ocp-Apim-Subscription-Key"] = "credential-value",
        };

        await using var result = await Gate(http).FollowAllowedRedirectsAsync(
            new HttpRequestSpec { Url = new Uri(source), Headers = headers },
            Allowlist,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(headers, http.Requests[0].Headers);
        Assert.Equal("application/octet-stream", http.Requests[1].Headers["Accept"]);
        Assert.DoesNotContain("Authorization", http.Requests[1].Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", http.Requests[1].Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Api-Key", http.Requests[1].Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ocp-Apim-Subscription-Key", http.Requests[1].Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redirect_follower_redacts_signed_cdn_urls_from_request_failures()
    {
        const string source = "https://github.com/x/setup.exe";
        var signature = new string('s', 32);
        var target = $"https://objects.github.com/asset/setup.exe?X-Amz-Credential=credential-value&X-Amz-Signature={signature}";
        var http = new StubHttpGateway()
            .Map(source, StubResponse.Redirect(target))
            .Map(target, _ => throw new InvalidOperationException($"Could not reach {target}"));

        await using var result = await Gate(http).FollowAllowedRedirectsAsync(
            new HttpRequestSpec { Url = new Uri(source) },
            Allowlist,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.RequestFailed, result.FailureStatus);
        Assert.DoesNotContain("credential-value", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(WgFetch.Core.Logging.SecretRedactor.Placeholder, result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepts_a_plausible_vendor_installer()
    {
        const string url = "https://nighttime-imaging.eu/download/NINASetup.exe";
        var http = new StubHttpGateway().Map(url, _ => StubResponse.Binary(FakeInstaller.PortableExecutable()) with
        {
            StatusCode = 206,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/octet-stream",
                ["Accept-Ranges"] = "bytes",
                ["ETag"] = "\"abc123\"",
            },
        });

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(InstallerFormat.PortableExecutable, result.Format);
        Assert.Equal(FakeInstaller.DefaultSize, result.ContentLength);
        Assert.Equal("\"abc123\"", result.Validator);
        Assert.True(result.AcceptsRanges);
    }

    [Fact]
    public async Task Rejects_a_hallucinated_domain_before_any_request_is_made()
    {
        var http = new StubHttpGateway();

        var result = await Gate(http).VerifyAsync(
            new Uri("https://nina-downloads-cdn.example.com/NINASetup.exe"),
            Allowlist,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.HostNotAllowlisted, result.Status);
        Assert.Empty(http.Requests);
        Assert.Equal(ExitCode.VerificationFailed, result.ToExitCode());
    }

    [Fact]
    public async Task Rejects_plain_http_even_on_an_allowlisted_host()
    {
        var http = new StubHttpGateway();

        var result = await Gate(http).VerifyAsync(
            new Uri("http://nighttime-imaging.eu/download/NINASetup.exe"),
            Allowlist,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.NotHttps, result.Status);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Rejects_a_redirect_that_leaves_the_allowlist()
    {
        const string url = "https://github.com/x/releases/download/v1/setup.exe";
        var http = new StubHttpGateway()
            .Map(url, StubResponse.Redirect("https://malicious.example.net/setup.exe"));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.RedirectOffAllowlist, result.Status);
        Assert.Contains("malicious.example.net", result.Reason);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Follows_redirects_that_stay_within_the_allowlist()
    {
        const string first = "https://github.com/x/releases/latest/download/setup.exe";
        const string second = "https://objects.github.com/asset/setup.exe";
        var http = new StubHttpGateway()
            .Map(first, StubResponse.Redirect(second))
            .Map(second, StubResponse.Binary(FakeInstaller.Msi()));

        var result = await Gate(http).VerifyAsync(new Uri(first), Allowlist, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(new Uri(second), result.FinalUrl);
        Assert.Equal(InstallerFormat.WindowsInstaller, result.Format);
    }

    [Fact]
    public async Task Rejects_a_redirect_to_http()
    {
        const string url = "https://github.com/x/setup.exe";
        var http = new StubHttpGateway().Map(url, StubResponse.Redirect("http://github.com/x/setup.exe"));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.NotHttps, result.Status);
    }

    [Fact]
    public async Task Rejects_a_login_page_served_with_status_200()
    {
        const string url = "https://nighttime-imaging.eu/download/NINASetup.exe";
        var http = new StubHttpGateway().Map(url, StubResponse.Html("<!DOCTYPE html><html><body>Please sign in</body></html>"));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.NotBinaryContentType, result.Status);
    }

    [Fact]
    public async Task Rejects_markup_disguised_with_a_binary_content_type()
    {
        const string url = "https://nighttime-imaging.eu/download/NINASetup.exe";
        var html = System.Text.Encoding.UTF8.GetBytes("<html><head><title>404 Not Found</title></head></html>");
        var http = new StubHttpGateway().Map(url, StubResponse.Binary(html));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.MarkupPayload, result.Status);
    }

    [Fact]
    public async Task Rejects_a_payload_whose_magic_bytes_match_no_installer_format()
    {
        const string url = "https://nighttime-imaging.eu/download/NINASetup.exe";
        var junk = new byte[64 * 1024];
        Array.Fill(junk, (byte)0x7F);
        var http = new StubHttpGateway().Map(url, StubResponse.Binary(junk));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.UnknownInstallerFormat, result.Status);
    }

    [Fact]
    public async Task Rejects_an_implausibly_small_payload()
    {
        const string url = "https://nighttime-imaging.eu/download/NINASetup.exe";
        var http = new StubHttpGateway().Map(url, StubResponse.Binary(FakeInstaller.PortableExecutable(1024)));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.ImplausibleLength, result.Status);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Reports_authentication_walls_with_a_dedicated_exit_code(int status)
    {
        const string url = "https://nighttime-imaging.eu/download/pro.exe";
        var http = new StubHttpGateway().Map(url, StubResponse.Status(status));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.RequiresAuthentication, result.Status);
        Assert.Equal(ExitCode.RequiresAuth, result.ToExitCode());
        Assert.Contains("requires authentication (P1, unsupported)", result.Reason);
    }

    [Fact]
    public async Task Reports_a_network_failure_rather_than_accepting_the_candidate()
    {
        const string url = "https://nighttime-imaging.eu/download/NINASetup.exe";
        var http = new StubHttpGateway().Map(url, StubResponse.Status(500));

        var result = await Gate(http).VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.RequestFailed, result.Status);
        Assert.Equal(ExitCode.NetworkError, result.ToExitCode());
    }

    [Fact]
    public async Task Stops_after_the_configured_redirect_limit()
    {
        const string url = "https://github.com/loop";
        var http = new StubHttpGateway().Map(url, StubResponse.Redirect(url));

        var gate = new VerificationGate(http, new VerificationOptions { MaximumRedirects = 3 });
        var result = await gate.VerifyAsync(new Uri(url), Allowlist, CancellationToken.None);

        Assert.Equal(VerificationStatus.TooManyRedirects, result.Status);
    }

    [Fact]
    public async Task Prompt_injection_in_page_content_cannot_move_a_url_past_the_gate()
    {
        // A page fetched during discovery instructs the model to download from an unrelated host.
        // The gate is mechanical: the instruction has no effect whatsoever.
        var injected = new Uri("https://totally-legit-mirror.example.org/NINASetup.exe");
        var http = new StubHttpGateway();

        var result = await Gate(http).VerifyAsync(injected, Allowlist, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(VerificationStatus.HostNotAllowlisted, result.Status);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task An_empty_allowlist_accepts_nothing()
    {
        var http = new StubHttpGateway();

        var result = await Gate(http).VerifyAsync(
            new Uri("https://github.com/x/setup.exe"),
            DomainAllowlist.Empty,
            CancellationToken.None);

        Assert.Equal(VerificationStatus.HostNotAllowlisted, result.Status);
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_accepting()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var http = new StubHttpGateway();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Gate(http).VerifyAsync(new Uri("https://github.com/x/setup.exe"), Allowlist, cts.Token));
    }
}
