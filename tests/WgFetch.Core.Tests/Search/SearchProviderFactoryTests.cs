using WgFetch.Core.Search;
using WgFetch.Core.Tests.Discovery;
using Xunit;

namespace WgFetch.Core.Tests.Search;

/// <summary>
/// Covers <c>--search-provider none</c> full support and provider selection
/// (docs/REQUIREMENTS.md, "Web search — pluggable, no hardcoded engine").
/// </summary>
public sealed class SearchProviderFactoryTests
{
    [Fact]
    public async Task NoneProvider_AlwaysReturnsZeroResults()
    {
        var provider = new NoneSearchProvider();
        var results = await provider.SearchAsync("anything", 5, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal("none", provider.Name);
    }

    [Fact]
    public void Factory_Create_None_ReturnsNoneSearchProvider()
    {
        var http = new FakeHttpGateway();
        var provider = SearchProviderFactory.Create("none", http);
        Assert.IsType<NoneSearchProvider>(provider);
    }

    [Fact]
    public void Factory_Create_DuckDuckGo_IsDefaultAndNeedsNoAccount()
    {
        var http = new FakeHttpGateway();
        var provider = SearchProviderFactory.Create("duckduckgo", http);
        Assert.Equal("duckduckgo", provider.Name);
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("google")]
    [InlineData("bing")]
    [InlineData("mojeek")]
    public void Factory_Create_KeyRequiringProviders_ThrowWithoutKey(string name)
    {
        var http = new FakeHttpGateway();
        Assert.Throws<ArgumentException>(() => SearchProviderFactory.Create(name, http));
    }

    [Fact]
    public void Factory_Create_UnknownProviderName_ThrowsClearError()
    {
        var http = new FakeHttpGateway();
        var ex = Assert.Throws<ArgumentException>(() => SearchProviderFactory.Create("not-a-real-provider", http));
        Assert.Contains("not-a-real-provider", ex.Message);
    }

    [Fact]
    public void Factory_Create_SearXng_RequiresInstanceEndpoint()
    {
        var http = new FakeHttpGateway();
        Assert.Throws<ArgumentException>(() => SearchProviderFactory.Create("searxng", http));

        var withEndpoint = SearchProviderFactory.Create(
            "searxng",
            http,
            new SearchProviderOptions { Endpoint = "https://searx.example.org" });
        Assert.Equal("searxng", withEndpoint.Name);
    }

    [Fact]
    public void Factory_Create_Brave_WithKey_Succeeds()
    {
        var http = new FakeHttpGateway();
        var provider = SearchProviderFactory.Create("brave", http, new SearchProviderOptions { Key = "test-key" });
        Assert.Equal("brave", provider.Name);
    }
}
