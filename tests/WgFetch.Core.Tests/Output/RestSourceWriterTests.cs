using WgFetch.Core.Output;

namespace WgFetch.Core.Tests.Output;

public sealed class RestSourceWriterTests : IDisposable
{
    private readonly string _outputRoot;

    public RestSourceWriterTests()
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

    [Fact]
    public async Task WriteInformationAsync_MatchesGolden()
    {
        var writer = new RestSourceWriter();
        await writer.WriteInformationAsync(_outputRoot, CancellationToken.None);

        var actualPath = SourceLayout.RestInformationPath(_outputRoot);
        AssertMatchesGolden(actualPath, Path.Combine(AppContext.BaseDirectory, "Golden", "output", "rest", "information.json"));
    }

    [Fact]
    public async Task WritePackageManifestAsync_MatchesGolden()
    {
        var writer = new RestSourceWriter();

        var version = new RestPackageVersion
        {
            PackageVersion = "3.2.0.9001",
            DefaultLocale = new RestDefaultLocale
            {
                PackageLocale = "en-US",
                Publisher = "Stefan Berg",
                PackageName = "N.I.N.A. - Nighttime Imaging 'N' Astronomy",
                License = "Freeware",
                ShortDescription = "Astrophotography imaging suite",
            },
            Installers =
            [
                new RestInstaller
                {
                    Architecture = "x64",
                    InstallerType = "zip",
                    InstallerUrl = "file:///output/installers/AstroStack.NINA/3.2.0.9001/NINASetupBundle_3.2.0.9001.zip",
                    InstallerSha256 = "ccfefa7a79cfb933fbad994853810daf6d78de8667aac052686d0a00fd9f1590",
                    NestedInstallerType = "exe",
                    NestedInstallerFiles = [new RestNestedInstallerFile { RelativeFilePath = "NINA.exe" }],
                },
            ],
        };

        await writer.WritePackageManifestAsync(_outputRoot, "AstroStack.NINA", [version], CancellationToken.None);

        var actualPath = SourceLayout.RestPackageManifestPath(_outputRoot, "AstroStack.NINA");
        AssertMatchesGolden(
            actualPath,
            Path.Combine(AppContext.BaseDirectory, "Golden", "output", "rest", "packageManifests", "AstroStack.NINA.json"));
    }

    private static void AssertMatchesGolden(string actualPath, string goldenPath)
    {
        Assert.True(File.Exists(goldenPath), $"golden file missing: {goldenPath}");
        var actual = File.ReadAllText(actualPath).ReplaceLineEndings("\n");
        var golden = File.ReadAllText(goldenPath).ReplaceLineEndings("\n");
        Assert.Equal(golden, actual);
    }
}
