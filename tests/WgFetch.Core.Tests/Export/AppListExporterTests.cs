using WgFetch.Core.Export;
using WgFetch.Core.Targets;

namespace WgFetch.Core.Tests.Export;

public class AppListExporterTests
{
    private static string GoldenPath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Golden", "targets", relative);

    private static TargetsDocument BuildCanonicalDocument()
    {
        var doc = new TargetsDocument();

        TargetEntry nina = doc.Add("nina");
        nina.Id = "AstroStack.NINA";
        nina.ComponentId = "nina";
        nina.State = TargetState.Acquired;
        nina.AcquiredVersion = "3.2.0.9001";
        nina.AvailableVersion = "3.2.0.9001";
        nina.Arch = "x64";
        nina.Scope = "machine";
        nina.Allowlist.Add("nighttime-imaging.eu");
        nina.Allowlist.Add("github.com");
        nina.LastAttempt = new DateTimeOffset(2026, 9, 10, 14, 22, 11, TimeSpan.Zero);

        TargetEntry sharpcap = doc.Add("sharpcap");
        sharpcap.ComponentId = "sharpcap";

        return doc;
    }

    [Fact]
    public void Render_MatchesGoldenFile()
    {
        var exporter = new AppListExporter();

        string rendered = exporter.Render(BuildCanonicalDocument());

        string expected = File.ReadAllText(GoldenPath("applist.yaml"));
        Assert.Equal(Normalize(expected), Normalize(rendered));
    }

    [Fact]
    public void Render_OmitsRunLocalAcquisitionState()
    {
        var exporter = new AppListExporter();

        string rendered = exporter.Render(BuildCanonicalDocument());

        Assert.DoesNotContain("state:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("acquiredVersion:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("availableVersion:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("lastAttempt:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("lastError:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_WritesFile_Atomically()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, $"applist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "applist.yaml");

        try
        {
            var exporter = new AppListExporter();
            await exporter.ExportAsync(BuildCanonicalDocument(), path);

            Assert.True(File.Exists(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}
