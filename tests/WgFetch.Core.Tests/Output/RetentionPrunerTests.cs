using WgFetch.Core.Output;

namespace WgFetch.Core.Tests.Output;

public sealed class RetentionPrunerTests : IDisposable
{
    private readonly string _root;

    public RetentionPrunerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wgfetch-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AcquiredVersionArtifacts MakeVersion(string version, string installerFileName = "app.exe")
    {
        var manifestDir = Path.Combine(_root, "manifests", version);
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, "app.yaml"), "PackageVersion: " + version);

        var installerDir = Path.Combine(_root, "installers", version);
        Directory.CreateDirectory(installerDir);
        var installerPath = Path.Combine(installerDir, installerFileName);
        File.WriteAllText(installerPath, "binary-stand-in");

        return new AcquiredVersionArtifacts
        {
            Version = version,
            ManifestDirectory = manifestDir,
            InstallerPaths = [installerPath],
        };
    }

    [Fact]
    public async Task PruneAsync_KeepsOnlyTheNMostRecentVersionsByDefault()
    {
        var versions = new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0" }.Select(v => MakeVersion(v)).ToList();

        var pruner = new RetentionPruner();
        var result = await pruner.PruneAsync(versions, pinnedVersion: null, keepVersions: RetentionPruner.DefaultKeepVersions, CancellationToken.None);

        Assert.Equal(["1.3.0", "1.2.0"], result.KeptVersions);
        Assert.Equal(["1.1.0", "1.0.0"], result.PrunedVersions);
    }

    [Fact]
    public async Task PruneAsync_DeletesBothInstallerAndManifestForPrunedVersions()
    {
        var versions = new[] { "1.0.0", "1.1.0", "1.2.0" }.Select(v => MakeVersion(v)).ToList();
        var pruned = versions.First(v => v.Version == "1.0.0");

        var pruner = new RetentionPruner();
        await pruner.PruneAsync(versions, pinnedVersion: null, keepVersions: 2, CancellationToken.None);

        Assert.False(Directory.Exists(pruned.ManifestDirectory));
        foreach (var installer in pruned.InstallerPaths)
        {
            Assert.False(File.Exists(installer));
        }
    }

    [Fact]
    public async Task PruneAsync_NeverPrunesAPinnedVersionEvenWhenStale()
    {
        var versions = new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0" }.Select(v => MakeVersion(v)).ToList();

        var pruner = new RetentionPruner();
        var result = await pruner.PruneAsync(versions, pinnedVersion: "1.0.0", keepVersions: 2, CancellationToken.None);

        Assert.Contains("1.0.0", result.KeptVersions);
        Assert.DoesNotContain("1.0.0", result.PrunedVersions);

        var pinned = versions.First(v => v.Version == "1.0.0");
        Assert.True(Directory.Exists(pinned.ManifestDirectory));
        Assert.All(pinned.InstallerPaths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task PruneAsync_NeverOrphansAKeptVersionsArtifacts()
    {
        var versions = new[] { "1.0.0", "1.1.0", "1.2.0" }.Select(v => MakeVersion(v)).ToList();

        var pruner = new RetentionPruner();
        var result = await pruner.PruneAsync(versions, pinnedVersion: null, keepVersions: 2, CancellationToken.None);

        foreach (var keptVersion in result.KeptVersions)
        {
            var artifact = versions.First(v => v.Version == keptVersion);
            Assert.True(Directory.Exists(artifact.ManifestDirectory), $"manifest for kept version {keptVersion} must survive");
            Assert.All(artifact.InstallerPaths, p => Assert.True(File.Exists(p), $"installer for kept version {keptVersion} must survive"));
        }
    }

    [Fact]
    public async Task PruneAsync_HandlesDateStampedAndMixedVersionFormats()
    {
        var versions = new[] { "2024.11.1", "2025.3.2", "2025.9.1" }.Select(v => MakeVersion(v)).ToList();

        var pruner = new RetentionPruner();
        var result = await pruner.PruneAsync(versions, pinnedVersion: null, keepVersions: 2, CancellationToken.None);

        Assert.Equal(["2025.9.1", "2025.3.2"], result.KeptVersions);
        Assert.Equal(["2024.11.1"], result.PrunedVersions);
    }
}
