using WgFetch.Core.Output;

namespace WgFetch.Core.Tests.Output;

public sealed class WingetManifestWriterTests : IDisposable
{
    private const string Sha256 = "ccfefa7a79cfb933fbad994853810daf6d78de8667aac052686d0a00fd9f1590";

    private readonly string _outputRoot;

    public WingetManifestWriterTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), "wgfetch-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_outputRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    private static WingetManifestRequest NinaRequest() => new()
    {
        PackageIdentifier = "AstroStack.NINA",
        PackageVersion = "3.2.0.9001",
        Publisher = "Stefan Berg",
        PackageName = "N.I.N.A. - Nighttime Imaging 'N' Astronomy",
        PackageUrl = "https://nighttime-imaging.eu/",
        License = "Freeware",
        ShortDescription = "Astrophotography imaging suite",
        Installers =
        [
            new WingetInstallerEntry
            {
                Architecture = "x64",
                InstallerType = "zip",
                InstallerUrl = new Uri("file:///output/installers/AstroStack.NINA/3.2.0.9001/NINASetupBundle_3.2.0.9001.zip"),
                InstallerSha256 = Sha256,
                NestedInstallerType = "exe",
                NestedInstallerFiles = ["NINA.exe"],
            },
        ],
    };

    [Fact]
    public async Task WriteAsync_EmitsGoldenThreeFileManifestSet()
    {
        var writer = new WingetManifestWriter();
        var result = await writer.WriteAsync(_outputRoot, NinaRequest(), CancellationToken.None);

        Assert.True(result.Success, result.Reason);

        var goldenRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Golden", "output", "manifests", "a", "AstroStack", "NINA", "3.2.0.9001");

        AssertMatchesGolden(result.VersionManifestPath!, Path.Combine(goldenRoot, "AstroStack.NINA.yaml"));
        AssertMatchesGolden(result.InstallerManifestPath!, Path.Combine(goldenRoot, "AstroStack.NINA.installer.yaml"));
        AssertMatchesGolden(result.LocaleManifestPath!, Path.Combine(goldenRoot, "AstroStack.NINA.locale.en-US.yaml"));
    }

    [Fact]
    public async Task WriteAsync_PlacesManifestUnderWingetPkgsConvention()
    {
        var writer = new WingetManifestWriter();
        var result = await writer.WriteAsync(_outputRoot, NinaRequest(), CancellationToken.None);

        var expectedDir = SourceLayout.ManifestDirectory(_outputRoot, "AstroStack.NINA", "3.2.0.9001");
        Assert.Equal(Path.Combine(expectedDir, "AstroStack.NINA.yaml"), result.VersionManifestPath);
        Assert.True(File.Exists(result.VersionManifestPath));
        Assert.True(File.Exists(result.InstallerManifestPath));
        Assert.True(File.Exists(result.LocaleManifestPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task WriteAsync_RefusesWhenVersionMissing(string? version)
    {
        var writer = new WingetManifestWriter();
        var request = NinaRequest() with { PackageVersion = version! };

        var result = await writer.WriteAsync(_outputRoot, request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.VersionManifestPath);
        AssertNoManifestsWritten();
    }

    [Fact]
    public async Task WriteAsync_RefusesWhenInstallerSha256Missing()
    {
        var writer = new WingetManifestWriter();
        var request = NinaRequest() with
        {
            Installers = [NinaRequest().Installers[0] with { InstallerSha256 = "" }],
        };

        var result = await writer.WriteAsync(_outputRoot, request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("InstallerSha256", result.Reason);
        AssertNoManifestsWritten();
    }

    [Fact]
    public async Task WriteAsync_RefusesWhenNoInstallers()
    {
        var writer = new WingetManifestWriter();
        var request = NinaRequest() with { Installers = [] };

        var result = await writer.WriteAsync(_outputRoot, request, CancellationToken.None);

        Assert.False(result.Success);
        AssertNoManifestsWritten();
    }

    [Fact]
    public async Task WriteAsync_RefusesZipBundleWithoutNestedInstallerType()
    {
        var writer = new WingetManifestWriter();
        var request = NinaRequest() with
        {
            Installers = [NinaRequest().Installers[0] with { NestedInstallerType = null }],
        };

        var result = await writer.WriteAsync(_outputRoot, request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("NestedInstallerType", result.Reason);
        AssertNoManifestsWritten();
    }

    private void AssertNoManifestsWritten()
    {
        var manifestsDir = Path.Combine(_outputRoot, SourceLayout.ManifestsDirectoryName);
        if (!Directory.Exists(manifestsDir))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFiles(manifestsDir, "*", SearchOption.AllDirectories));
    }

    private static void AssertMatchesGolden(string actualPath, string goldenPath)
    {
        Assert.True(File.Exists(goldenPath), $"golden file missing: {goldenPath}");
        var actual = File.ReadAllText(actualPath).ReplaceLineEndings("\n");
        var golden = File.ReadAllText(goldenPath).ReplaceLineEndings("\n");
        Assert.Equal(golden, actual);
    }
}
