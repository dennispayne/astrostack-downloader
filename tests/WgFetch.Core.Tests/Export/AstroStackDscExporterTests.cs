using System.Text.Json;
using WgFetch.Core.Export;

namespace WgFetch.Core.Tests.Export;

public class AstroStackDscExporterTests
{
    private static string GoldenPath(params string[] relative) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "Golden", "export" }.Concat(relative).ToArray());

    private static List<AstroStackDscExportItem> BuildFixtureItems() => new()
    {
        new AstroStackDscExportItem
        {
            ComponentId = "nina",
            DownloadUrl = "https://github.com/isbeorn/nina/releases/download/v3.2.0.9001/NINASetupBundle_3.2.0.9001.zip",
            DownloadFileName = "NINASetupBundle_3.2.0.9001.zip",
            Sha256 = "4273b751aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Verified = true,
            AvailableVersion = "3.2.0.9001",
            InstallerRelativePath = "installers/AstroStack.NINA/3.2.0.9001/NINASetupBundle_3.2.0.9001.zip",
            PreviousAvailableVersion = "3.2.0.9001",
        },
        new AstroStackDscExportItem
        {
            ComponentId = "astap",
            DownloadUrl = "https://www.hnsky.org/astap_x64.zip",
            DownloadFileName = "astap_x64.zip",
            Sha256 = "b19e1f1a1111111111111111111111111111111111111111111111111111111",
            Verified = true,
            AvailableVersion = "2024.09.15",
            InstallerRelativePath = "installers/AstroStack.ASTAP/2024.09.15/astap_x64.zip",
            PreviousAvailableVersion = "2024.06.01",
        },
        new AstroStackDscExportItem
        {
            ComponentId = "sharpcap",
            Verified = false,
        },
        new AstroStackDscExportItem
        {
            ComponentId = "astap-db-h18",
            Verified = false,
        },
    };

    [Fact]
    public async Task ExportAsync_ProducesGoldenPatchFiles()
    {
        string outDir = Path.Combine(AppContext.BaseDirectory, $"export-{Guid.NewGuid():N}");
        try
        {
            var exporter = new AstroStackDscExporter();
            int count = await exporter.ExportAsync(BuildFixtureItems(), outDir);

            Assert.Equal(4, count);

            foreach (string componentId in new[] { "nina", "astap", "sharpcap", "astap-db-h18" })
            {
                string actual = await File.ReadAllTextAsync(Path.Combine(outDir, "patches", $"{componentId}.json"));
                string expected = await File.ReadAllTextAsync(GoldenPath("patches", $"{componentId}.json"));
                Assert.Equal(Normalize(expected), Normalize(actual));
            }

            string actualMapping = await File.ReadAllTextAsync(Path.Combine(outDir, "path-mapping.json"));
            string expectedMapping = await File.ReadAllTextAsync(GoldenPath("path-mapping.json"));
            Assert.Equal(Normalize(expectedMapping), Normalize(actualMapping));

            string actualSummary = await File.ReadAllTextAsync(Path.Combine(outDir, "summary.json"));
            string expectedSummary = await File.ReadAllTextAsync(GoldenPath("summary.json"));
            Assert.Equal(Normalize(expectedSummary), Normalize(actualSummary));
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportAsync_Patches_NeverContainHumanOwnedFields()
    {
        string[] humanOwnedFields =
        {
            "expectedVersion", "checkPath", "versionSource", "versionStrategy", "versionRegex",
            "silentInstallArgs", "archiveContainsInstaller", "module", "kind", "notes",
            "installNotes", "displayName",
        };

        string outDir = Path.Combine(AppContext.BaseDirectory, $"export-human-owned-{Guid.NewGuid():N}");
        try
        {
            var exporter = new AstroStackDscExporter();
            await exporter.ExportAsync(BuildFixtureItems(), outDir);

            foreach (string patchFile in Directory.GetFiles(Path.Combine(outDir, "patches"), "*.json"))
            {
                using JsonDocument patch = JsonDocument.Parse(await File.ReadAllTextAsync(patchFile));
                var keys = patch.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

                foreach (string humanField in humanOwnedFields)
                {
                    Assert.DoesNotContain(humanField, keys);
                }

                // Only the join key plus the five machine-owned fields may ever appear.
                var allowed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "id", "downloadUrl", "downloadFileName", "sha256", "verified", "availableVersion",
                };
                Assert.True(keys.IsSubsetOf(allowed), $"Unexpected keys in {patchFile}: {string.Join(", ", keys.Except(allowed))}");
            }
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportAsync_ListedComponents_NeverClaimVerifiedTrue_AndHaveNoDownloadUrl()
    {
        string outDir = Path.Combine(AppContext.BaseDirectory, $"export-listed-{Guid.NewGuid():N}");
        try
        {
            var exporter = new AstroStackDscExporter();
            await exporter.ExportAsync(BuildFixtureItems(), outDir);

            foreach (string componentId in new[] { "sharpcap", "astap-db-h18" })
            {
                using JsonDocument patch = JsonDocument.Parse(
                    await File.ReadAllTextAsync(Path.Combine(outDir, "patches", $"{componentId}.json")));

                JsonElement verified = patch.RootElement.GetProperty("verified");
                Assert.False(verified.ValueKind == JsonValueKind.True);

                JsonElement downloadUrl = patch.RootElement.GetProperty("downloadUrl");
                Assert.Equal(JsonValueKind.Null, downloadUrl.ValueKind);
            }
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}
