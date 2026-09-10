using WgFetch.Core.Catalog;
using WgFetch.Core.Inference;
using Xunit;

namespace WgFetch.Core.Tests.Inference;

/// <summary>
/// Tier-1/Tier-2 name resolution behaviour with deterministic fakes, including ambiguity
/// (docs/REQUIREMENTS.md, "Name resolution", "Testing").
/// </summary>
public sealed class NameResolverTests
{
    private static readonly CatalogEntry[] Catalog =
    [
        new CatalogEntry { Id = "nina", DisplayName = "N.I.N.A.", Publisher = "Stefan Berg", Tags = ["astrophotography", "capture"], Aliases = ["nighttime imaging n and a"] },
        new CatalogEntry { Id = "phd2", DisplayName = "PHD2 Guiding", Publisher = "Open PHD Guiding", Tags = ["guiding", "astrophotography"] },
        new CatalogEntry { Id = "sharpcap", DisplayName = "SharpCap", Publisher = "SharpCap Ltd", Tags = ["capture", "planetary"] },
        new CatalogEntry { Id = "sharpcap-pro", DisplayName = "SharpCap Pro", Publisher = "SharpCap Ltd", Tags = ["capture", "planetary", "paid"] },
    ];

    [Fact]
    public async Task Tier1_ExactNameMatch_ResolvesConfidently_WithoutTier2()
    {
        var resolver = new NameResolver(Catalog, new FakeEmbeddingModel());
        await resolver.WarmAsync(CancellationToken.None);

        var result = await resolver.ResolveAsync("N.I.N.A.", threshold: 0.3, CancellationToken.None);

        Assert.False(result.IsAmbiguous);
        Assert.False(result.UsedTier2);
        Assert.Equal("nina", result.Best!.Entry.Id);
    }

    [Fact]
    public async Task Tier1_NoOverlap_IsAmbiguous_WithoutTier2Configured()
    {
        var resolver = new NameResolver(Catalog, new FakeEmbeddingModel());
        await resolver.WarmAsync(CancellationToken.None);

        var result = await resolver.ResolveAsync("completely unrelated gibberish zzz", threshold: 0.3, CancellationToken.None);

        Assert.True(result.IsAmbiguous);
        Assert.False(result.UsedTier2);
        Assert.NotEmpty(result.Candidates); // Tier-1 candidates still surfaced even when ambiguous
    }

    [Fact]
    public async Task ClusteredTopCandidates_Escalates_ToTier2_AndPicksChosenId()
    {
        var tier2 = new ScriptedTextGenerator().Enqueue("CHOSEN_ID: sharpcap-pro");
        var resolver = new NameResolver(Catalog, new FakeEmbeddingModel(), tier2, clusterMargin: 1.0); // force clustering
        await resolver.WarmAsync(CancellationToken.None);

        var result = await resolver.ResolveAsync("sharpcap", threshold: 0.0, CancellationToken.None);

        Assert.True(result.UsedTier2);
        Assert.False(result.IsAmbiguous);
        Assert.Equal("sharpcap-pro", result.Best!.Entry.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage output with no marker")]
    [InlineData("CHOSEN_ID: not-a-real-id")]
    [InlineData("CHOSEN_ID: none")]
    public async Task Tier2_MalformedOrUnresolvedOutput_FailsClosed_KeepsTier1Order(string tier2Output)
    {
        var tier2 = new ScriptedTextGenerator().Enqueue(tier2Output);
        var resolver = new NameResolver(Catalog, new FakeEmbeddingModel(), tier2, clusterMargin: 1.0);
        await resolver.WarmAsync(CancellationToken.None);

        var result = await resolver.ResolveAsync("sharpcap", threshold: 0.0, CancellationToken.None);

        Assert.True(result.UsedTier2);
        Assert.True(result.IsAmbiguous);
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public async Task Tier2_GeneratorThrows_FailsClosed_ToAmbiguousTier1Result()
    {
        var tier2 = new ScriptedTextGenerator().EnqueueThrow(new InvalidOperationException("model unavailable"));
        var resolver = new NameResolver(Catalog, new FakeEmbeddingModel(), tier2, clusterMargin: 1.0);
        await resolver.WarmAsync(CancellationToken.None);

        var result = await resolver.ResolveAsync("sharpcap", threshold: 0.0, CancellationToken.None);

        Assert.True(result.IsAmbiguous);
        Assert.True(result.UsedTier2);
    }

    [Fact]
    public async Task EmptyCatalog_IsAlwaysAmbiguous()
    {
        var resolver = new NameResolver(Array.Empty<CatalogEntry>(), new FakeEmbeddingModel());
        var result = await resolver.ResolveAsync("anything", threshold: 0.3, CancellationToken.None);

        Assert.True(result.IsAmbiguous);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Tier1_IsFast_ForWarmCatalog()
    {
        var resolver = new NameResolver(Catalog, new FakeEmbeddingModel());
        await resolver.WarmAsync(CancellationToken.None);

        // Prime once, then measure a subsequent warm lookup.
        await resolver.ResolveAsync("nina", threshold: 0.3, CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await resolver.ResolveAsync("phd2 guiding", threshold: 0.3, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"Tier-1 lookup took too long: {sw.Elapsed}");
    }
}
