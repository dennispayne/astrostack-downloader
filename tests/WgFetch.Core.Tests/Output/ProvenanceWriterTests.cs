using WgFetch.Core.Output;

namespace WgFetch.Core.Tests.Output;

public sealed class ProvenanceWriterTests : IDisposable
{
    private readonly string _outputRoot;

    public ProvenanceWriterTests()
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

    private static ProvenanceRecord NinaRecord() => new()
    {
        PackageIdentifier = "AstroStack.NINA",
        ComponentId = "nina",
        FriendlyQuery = "nina imaging suite (leaked token ghp_ABCDEFGHIJKLMNOPQRSTUVWX1234 should never appear)",
        DiscoveryStage = "LlmAssisted",
        Candidates =
        [
            new ProvenanceCandidate
            {
                Url = "https://nighttime-imaging.eu/download/NINASetupBundle_3.2.0.9001.zip",
                Stage = "LlmAssisted",
                Rationale = "download page mentions credentials sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456 in a tooltip",
                VerificationStatus = "Accepted",
                Reason = "accepted: Zip, 52428800 bytes",
                Accepted = true,
            },
        ],
        AcceptedUrl = "https://nighttime-imaging.eu/download/NINASetupBundle_3.2.0.9001.zip",
        ResolvedVersion = "3.2.0.9001",
        SourceVersions = new Dictionary<string, string>(StringComparer.Ordinal) { ["github"] = "3.2.0.9001" },
        Sha256 = "ccfefa7a79cfb933fbad994853810daf6d78de8667aac052686d0a00fd9f1590",
        Timestamp = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
        ResolutionTier = "LlmAssisted",
        Confidence = 0.92,
        AiMode = "local",
        RecipeUsed = true,
        RecipeOrigin = "Seed",
    };

    [Fact]
    public async Task WriteAsync_MatchesGolden()
    {
        var writer = new ProvenanceWriter();
        await writer.WriteAsync(_outputRoot, NinaRecord(), CancellationToken.None);

        var actual = (await File.ReadAllTextAsync(SourceLayout.ProvenancePath(_outputRoot))).ReplaceLineEndings("\n");
        var golden = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Golden", "output", "provenance.json")))
            .ReplaceLineEndings("\n");

        Assert.Equal(golden, actual);
    }

    [Fact]
    public async Task WriteAsync_RedactsKnownSecretsEverywhere()
    {
        const string githubToken = "ghp_ABCDEFGHIJKLMNOPQRSTUVWX1234";
        const string openAiKey = "sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";

        var writer = new ProvenanceWriter();
        await writer.WriteAsync(_outputRoot, NinaRecord(), CancellationToken.None);

        var text = await File.ReadAllTextAsync(SourceLayout.ProvenancePath(_outputRoot));

        Assert.DoesNotContain(githubToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(openAiKey, text, StringComparison.Ordinal);
        Assert.Contains(WgFetch.Core.Logging.SecretRedactor.Placeholder, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_ReplacesRecordForSamePackageAndVersionIdempotently()
    {
        var writer = new ProvenanceWriter();

        await writer.WriteAsync(_outputRoot, NinaRecord(), CancellationToken.None);
        await writer.WriteAsync(_outputRoot, NinaRecord() with { Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"[..64] }, CancellationToken.None);

        var records = await writer.ReadAllAsync(_outputRoot, CancellationToken.None);

        var forPackage = records.Where(r =>
            r.PackageIdentifier == "AstroStack.NINA" && r.ResolvedVersion == "3.2.0.9001").ToList();

        Assert.Single(forPackage);
        Assert.Equal("0000000000000000000000000000000000000000000000000000000000000000"[..64], forPackage[0].Sha256);
    }

    [Fact]
    public async Task WriteAsync_KeepsRecordsForDifferentVersionsSeparate()
    {
        var writer = new ProvenanceWriter();

        await writer.WriteAsync(_outputRoot, NinaRecord(), CancellationToken.None);
        await writer.WriteAsync(_outputRoot, NinaRecord() with { ResolvedVersion = "3.2.0.9002" }, CancellationToken.None);

        var records = await writer.ReadAllAsync(_outputRoot, CancellationToken.None);
        Assert.Equal(2, records.Count(r => r.PackageIdentifier == "AstroStack.NINA"));
    }
}
