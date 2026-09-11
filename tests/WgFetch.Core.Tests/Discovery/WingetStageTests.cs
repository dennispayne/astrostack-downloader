using WgFetch.Core.Discovery;
using Xunit;

namespace WgFetch.Core.Tests.Discovery;

/// <summary>
/// Parsing of <c>microsoft/winget-pkgs</c> installer manifests against a fake GitHub contents API.
/// The manifest reader deliberately uses YamlDotNet's reflection-free representation model so the
/// stage keeps working in the NativeAOT binary (docs/REQUIREMENTS.md, "Discovery pipeline", stage 3).
/// </summary>
public sealed class WingetStageTests
{
    private const string VersionsUrl =
        "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/d/Vendor/Demo";

    private const string FilesUrl =
        "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/d/Vendor/Demo/1.2.3";

    private const string ManifestUrl = "https://raw.githubusercontent.com/demo/Vendor.Demo.installer.yaml";

    private static DiscoveryRequest Request() => new()
    {
        Query = "demo",
        ComponentId = "demo",
        KnownWingetPackageId = "Vendor.Demo",
    };

    private static FakeHttpGateway GatewayFor(string manifestYaml) =>
        new FakeHttpGateway()
            .AddResponse(VersionsUrl, FakeHttpResponse.Json("""[{"name":"1.0.0","type":"dir"},{"name":"1.2.3","type":"dir"}]"""))
            .AddResponse(
                FilesUrl,
                FakeHttpResponse.Json(
                    $$"""[{"name":"Vendor.Demo.installer.yaml","type":"file","download_url":"{{ManifestUrl}}"}]"""))
            .AddResponse(ManifestUrl, FakeHttpResponse.Html(manifestYaml));

    [Fact]
    public async Task Reads_InstallerUrl_Architecture_And_UpstreamSha_FromNewestVersion()
    {
        var http = GatewayFor(
            """
            PackageIdentifier: Vendor.Demo
            PackageVersion: 1.2.3
            Installers:
              - Architecture: x64
                InstallerUrl: https://vendor.example/demo-1.2.3-x64.exe
                InstallerSha256: AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899
              - Architecture: x86
                InstallerUrl: https://vendor.example/demo-1.2.3-x86.exe
            """);

        var outcome = await new WingetStage(http).TryResolveAsync(Request(), CancellationToken.None);

        Assert.Equal("1.2.3", outcome.Version);
        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Equal("https://vendor.example/demo-1.2.3-x64.exe", outcome.Candidates[0].Url.ToString());
        Assert.Contains("x64", outcome.Candidates[0].Rationale, StringComparison.Ordinal);
        Assert.Equal(
            "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
            outcome.Candidates[0].UpstreamSha256);
        Assert.Null(outcome.Candidates[1].UpstreamSha256);
        Assert.Equal(["vendor.example"], outcome.Allowlist);
    }

    [Fact]
    public async Task Falls_Back_To_UnspecifiedArch_WhenArchitectureIsAbsent()
    {
        var http = GatewayFor(
            """
            Installers:
              - InstallerUrl: https://vendor.example/demo.exe
            """);

        var outcome = await new WingetStage(http).TryResolveAsync(Request(), CancellationToken.None);

        Assert.Contains("unspecified arch", Assert.Single(outcome.Candidates).Rationale, StringComparison.Ordinal);
    }

    [Theory]
    // A non-absolute URL, a nested mapping where a scalar is expected, and a missing key all drop
    // the entry rather than throwing or inventing a candidate.
    [InlineData("Installers:\n  - InstallerUrl: not-a-url\n")]
    [InlineData("Installers:\n  - InstallerUrl:\n      nested: https://vendor.example/demo.exe\n")]
    [InlineData("Installers:\n  - Architecture: x64\n")]
    [InlineData("Installers:\n  - just-a-scalar\n")]
    [InlineData("Installers: not-a-sequence\n")]
    [InlineData("PackageIdentifier: Vendor.Demo\n")]
    [InlineData("- a top-level sequence\n")]
    [InlineData("")]
    public async Task Unusable_Manifest_Shapes_Yield_No_Candidates(string manifestYaml)
    {
        var http = GatewayFor(manifestYaml);

        var outcome = await new WingetStage(http).TryResolveAsync(Request(), CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.NotNull(outcome.Warning);
    }

    [Fact]
    public async Task Malformed_Yaml_Is_Reported_Not_Thrown()
    {
        var http = GatewayFor("Installers:\n  - InstallerUrl: \"unterminated\n    Architecture: [x64\n");

        var outcome = await new WingetStage(http).TryResolveAsync(Request(), CancellationToken.None);

        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public async Task NonWingetShapedPackageId_ShortCircuits_WithoutNetworkCalls()
    {
        var http = new FakeHttpGateway();

        var outcome = await new WingetStage(http).TryResolveAsync(
            Request() with { KnownWingetPackageId = "Demo" },
            CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.Empty(http.RequestedUrls);
    }

    [Fact]
    public async Task AbsentPackageId_ShortCircuits_WithoutNetworkCalls()
    {
        var http = new FakeHttpGateway();

        var outcome = await new WingetStage(http).TryResolveAsync(
            Request() with { KnownWingetPackageId = null },
            CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.Empty(http.RequestedUrls);
    }
}
