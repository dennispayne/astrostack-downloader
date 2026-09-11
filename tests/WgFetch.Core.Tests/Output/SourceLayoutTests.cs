using WgFetch.Core.Output;

namespace WgFetch.Core.Tests.Output;

public sealed class SourceLayoutTests
{
    [Fact]
    public void ManifestDirectory_SplitsMultiSegmentIdentifierIntoNestedDirectories()
    {
        var path = SourceLayout.ManifestDirectory("/out", "AstroStack.NINA", "3.2.0.9001");
        Assert.Equal(Path.Combine("/out", "manifests", "a", "AstroStack", "NINA", "3.2.0.9001"), path);
    }

    [Fact]
    public void ManifestDirectory_UsesLowercaseFirstLetterOfIdentifier()
    {
        var path = SourceLayout.ManifestDirectory("/out", "ZebraCorp.Tool", "1.0.0");
        Assert.Contains(Path.Combine("manifests", "z"), path);
    }

    [Fact]
    public void InstallerPath_PreservesOriginalVendorFileName()
    {
        var path = SourceLayout.InstallerPath("/out", "AstroStack.NINA", "3.2.0.9001", "NINASetupBundle_3.2.0.9001.zip");
        Assert.EndsWith("NINASetupBundle_3.2.0.9001.zip", path);
    }

    [Fact]
    public void RestPackageManifestPath_UsesPackageIdentifierAsFileName()
    {
        var path = SourceLayout.RestPackageManifestPath("/out", "AstroStack.NINA");
        Assert.Equal(Path.Combine("/out", "rest", "packageManifests", "AstroStack.NINA.json"), path);
    }
}
