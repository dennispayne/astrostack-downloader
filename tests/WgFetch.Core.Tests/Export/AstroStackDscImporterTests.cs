using WgFetch.Core.Export;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Tests.Export;

public class AstroStackDscImporterTests
{
    // Copied next to the test assembly by the csproj. Resolved from AppContext.BaseDirectory rather
    // than [CallerFilePath], which CI builds rewrite to /_/... under ContinuousIntegrationBuild.
    private static string FixturesDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Export", "Fixtures", "components");

    [Fact]
    public async Task ImportAsync_RealAstroStackDscFixture_SeedsExpectedStates()
    {
        var importer = new AstroStackDscImporter();

        TargetsDocument doc = await importer.ImportAsync(FixturesDirectory());

        Assert.Equal(4, doc.Targets.Count);

        TargetEntry? nina = doc.Find("nina");
        Assert.NotNull(nina);
        Assert.Equal(TargetState.Acquired, nina!.State);
        Assert.Equal("nina", nina.ComponentId);
        Assert.Equal("3.2.0.9001", nina.AcquiredVersion);

        TargetEntry? astap = doc.Find("astap");
        Assert.NotNull(astap);
        Assert.Equal(TargetState.Acquired, astap!.State);
        Assert.Equal("2024.09.15", astap.AcquiredVersion);

        TargetEntry? sharpcap = doc.Find("sharpcap");
        Assert.NotNull(sharpcap);
        Assert.Equal(TargetState.Listed, sharpcap!.State);
        Assert.Null(sharpcap.AcquiredVersion);

        TargetEntry? astapDb = doc.Find("astap-db-h18");
        Assert.NotNull(astapDb);
        Assert.Equal(TargetState.Listed, astapDb!.State);
    }

    [Fact]
    public async Task ImportAsync_MissingDirectory_ReturnsEmptyDocument()
    {
        var importer = new AstroStackDscImporter();

        TargetsDocument doc = await importer.ImportAsync(
            Path.Combine(AppContext.BaseDirectory, $"does-not-exist-{Guid.NewGuid():N}"));

        Assert.Empty(doc.Targets);
    }

    [Fact]
    public async Task ImportAsync_MalformedJsonFile_IsSkipped_NotFatal()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, $"import-malformed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), "{ this is not valid json");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "ok.json"),
                """{"id":"ok-component","downloadUrl":null,"verified":false}""");

            var importer = new AstroStackDscImporter();
            TargetsDocument doc = await importer.ImportAsync(dir);

            TargetEntry entry = Assert.Single(doc.Targets);
            Assert.Equal("ok-component", entry.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_UnknownExtraJsonFields_DoNotBreakImport()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, $"import-extra-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "future.json"),
                """
                {
                  "id": "future-component",
                  "downloadUrl": "https://vendor.example/app.zip",
                  "verified": true,
                  "expectedVersion": "1.0.0",
                  "somethingWgFetchDoesNotKnowAboutYet": { "nested": [1, 2, 3] }
                }
                """);

            var importer = new AstroStackDscImporter();
            TargetsDocument doc = await importer.ImportAsync(dir);

            TargetEntry entry = Assert.Single(doc.Targets);
            Assert.Equal(TargetState.Acquired, entry.State);
            Assert.Equal("1.0.0", entry.AcquiredVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_PopulatedButUnverified_BecomesResolved_NotAcquired()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, $"import-resolved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "candidate.json"),
                """{"id":"candidate","downloadUrl":"https://vendor.example/app.zip","verified":false}""");

            var importer = new AstroStackDscImporter();
            TargetsDocument doc = await importer.ImportAsync(dir);

            TargetEntry entry = Assert.Single(doc.Targets);
            Assert.Equal(TargetState.Resolved, entry.State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
