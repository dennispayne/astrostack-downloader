using WgFetch.Core.Search;
using WgFetch.Core.Tests.Support;

namespace WgFetch.Core.Tests.Search;

/// <summary>
/// Every shipped provider, exercised through the same seam: an API request (never an HTML scrape),
/// robots.txt consulted first, and a parsed result set (docs/REQUIREMENTS.md, "Web search").
/// </summary>
public sealed class SearchProviderTests
{
    private const string Query = "nina astrophotography download";

    private static StubHttpGateway WithRobots(params string[] hosts)
    {
        var http = new StubHttpGateway();
        foreach (var host in hosts)
        {
            http.Map($"https://{host}/robots.txt", new StubResponse
            {
                Body = System.Text.Encoding.UTF8.GetBytes("User-agent: *\nDisallow: /private\n"),
                Headers = { ["Content-Type"] = "text/plain" },
            });
        }

        return http;
    }

    /// <summary>Maps a URL the way the gateway sees it: <see cref="Uri.ToString"/> normalizes escaping.</summary>
    private static StubHttpGateway MapUrl(StubHttpGateway http, string url, StubResponse response) =>
        http.Map(new Uri(url).ToString(), response);

    private static StubResponse Json(string body) => new()
    {
        Body = System.Text.Encoding.UTF8.GetBytes(body),
        Headers = { ["Content-Type"] = "application/json" },
    };

    private static async Task<IReadOnlyList<SearchResult>> SearchAsync(ISearchProvider provider) =>
        await provider.SearchAsync(Query, 5, CancellationToken.None);

    [Fact]
    public async Task DuckDuckGo_ParsesAbstractAndRelatedTopics()
    {
        var http = WithRobots("api.duckduckgo.com");
        MapUrl(
            http,
            $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(Query)}&format=json&no_html=1&no_redirect=1&skip_disambig=1",
            Json("""
                {
                  "AbstractURL": "https://nighttime-imaging.eu/",
                  "Heading": "N.I.N.A.",
                  "AbstractText": "Nighttime Imaging 'N' Astronomy",
                  "RelatedTopics": [
                    { "FirstURL": "https://nighttime-imaging.eu/download/", "Text": "Downloads" },
                    { "Topics": [ { "FirstURL": "https://nighttime-imaging.eu/docs/", "Text": "Docs" } ] }
                  ]
                }
                """));

        var results = await SearchAsync(new DuckDuckGoSearchProvider(http));

        Assert.Equal(3, results.Count);
        Assert.Equal("https://nighttime-imaging.eu/", results[0].Url.ToString());
        Assert.Equal("N.I.N.A.", results[0].Title);
        Assert.Contains(results, r => r.Url.ToString().EndsWith("/docs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Brave_SendsItsKeyAsAHeaderAndNeverInTheUrl()
    {
        var key = "brave-" + new string('k', 20);
        var http = WithRobots("api.search.brave.com");
        MapUrl(
            http,
            $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(Query)}&count=5",
            Json("""
                { "web": { "results": [ { "url": "https://vendor.example.com/", "title": "Vendor", "description": "Official" } ] } }
                """));

        var results = await SearchAsync(new BraveSearchProvider(http, key));

        var apiRequest = http.Requests.Single(r => r.Url.Host == "api.search.brave.com" && !r.Url.AbsolutePath.EndsWith("robots.txt", StringComparison.Ordinal));
        Assert.Equal(key, apiRequest.Headers["X-Subscription-Token"]);
        Assert.DoesNotContain(key, apiRequest.Url.ToString(), StringComparison.Ordinal);
        Assert.Equal("https://vendor.example.com/", Assert.Single(results).Url.ToString());
    }

    [Fact]
    public async Task Bing_ParsesWebPages()
    {
        var http = WithRobots("api.bing.microsoft.com");
        MapUrl(
            http,
            $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(Query)}&count=5",
            Json("""
                { "webPages": { "value": [ { "url": "https://vendor.example.com/a", "name": "A", "snippet": "s" } ] } }
                """));

        var results = await SearchAsync(new BingSearchProvider(http, "bing-key"));

        Assert.Equal("A", Assert.Single(results).Title);
    }

    [Fact]
    public async Task Google_ParsesItems()
    {
        var http = WithRobots("www.googleapis.com");
        MapUrl(
            http,
            $"https://www.googleapis.com/customsearch/v1?key=g-key&cx=cx-id&q={Uri.EscapeDataString(Query)}&num=5",
            Json("""
                { "items": [ { "link": "https://vendor.example.com/b", "title": "B", "snippet": "s" } ] }
                """));

        var results = await SearchAsync(new GoogleSearchProvider(http, "g-key", "cx-id"));

        Assert.Equal("B", Assert.Single(results).Title);
    }

    [Fact]
    public async Task Mojeek_ParsesResponseResults()
    {
        var http = WithRobots("www.mojeek.com");
        MapUrl(
            http,
            $"https://www.mojeek.com/services/search?q={Uri.EscapeDataString(Query)}&api_key=m-key&fmt=json&t=5",
            Json("""
                { "response": { "results": [ { "url": "https://vendor.example.com/c", "title": "C", "desc": "d" } ] } }
                """));

        var results = await SearchAsync(new MojeekSearchProvider(http, "m-key"));

        Assert.Equal("C", Assert.Single(results).Title);
    }

    [Fact]
    public async Task Startpage_UsesTheConfiguredEndpoint()
    {
        var http = WithRobots("sp.example.org");
        MapUrl(
            http,
            $"https://sp.example.org/?q={Uri.EscapeDataString(Query)}&format=json",
            Json("""
                { "results": [ { "url": "https://vendor.example.com/d", "title": "D", "snippet": "s" } ] }
                """));

        var results = await SearchAsync(new StartpageSearchProvider(http, "https://sp.example.org/"));

        Assert.Equal("D", Assert.Single(results).Title);
    }

    [Fact]
    public async Task SearXng_UsesTheSelfHostedInstance()
    {
        var http = WithRobots("searx.example.org");
        MapUrl(
            http,
            $"https://searx.example.org/search?q={Uri.EscapeDataString(Query)}&format=json",
            Json("""
                { "results": [ { "url": "https://vendor.example.com/e", "title": "E", "content": "s" } ] }
                """));

        var results = await SearchAsync(new SearXngSearchProvider(http, "https://searx.example.org/"));

        Assert.Equal("E", Assert.Single(results).Title);
    }

    [Fact]
    public async Task None_ContactsNothing()
    {
        var http = new StubHttpGateway();

        var results = await new NoneSearchProvider().SearchAsync(Query, 5, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task HttpFailure_YieldsNoResultsRatherThanThrowing()
    {
        var http = WithRobots("api.duckduckgo.com");
        MapUrl(
            http,
            $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(Query)}&format=json&no_html=1&no_redirect=1&skip_disambig=1",
            StubResponse.Status(503));

        var results = await SearchAsync(new DuckDuckGoSearchProvider(http));

        Assert.Empty(results);
    }

    [Fact]
    public async Task MalformedBody_YieldsNoResultsRatherThanThrowing()
    {
        var http = WithRobots("api.duckduckgo.com");
        MapUrl(
            http,
            $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(Query)}&format=json&no_html=1&no_redirect=1&skip_disambig=1",
            Json("not json at all"));

        var results = await SearchAsync(new DuckDuckGoSearchProvider(http));

        Assert.Empty(results);
    }

    [Fact]
    public async Task RobotsDisallow_SkipsTheProviderEntirely()
    {
        var http = new StubHttpGateway();
        http.Map("https://api.duckduckgo.com/robots.txt", new StubResponse
        {
            Body = System.Text.Encoding.UTF8.GetBytes("User-agent: *\nDisallow: /\n"),
            Headers = { ["Content-Type"] = "text/plain" },
        });

        var results = await SearchAsync(new DuckDuckGoSearchProvider(http));

        // The API URL is deliberately not mapped: reaching it at all would throw.
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("duckduckgo")]
    [InlineData("none")]
    public void Factory_CreatesKeylessProviders(string name)
    {
        var provider = SearchProviderFactory.Create(name, new StubHttpGateway());

        Assert.Equal(name, provider.Name);
    }

    [Fact]
    public void Factory_RefusesAKeyedProviderWithoutAKey()
    {
        Assert.ThrowsAny<Exception>(() => SearchProviderFactory.Create("brave", new StubHttpGateway()));
    }

    [Fact]
    public void Factory_RefusesAnUnknownProvider()
    {
        Assert.ThrowsAny<Exception>(() => SearchProviderFactory.Create("altavista", new StubHttpGateway()));
    }

    [Fact]
    public void Factory_KnowsEveryProviderNamedInTheSpec()
    {
        foreach (var name in new[] { "duckduckgo", "brave", "startpage", "mojeek", "searxng", "google", "bing", "none" })
        {
            Assert.Contains(name, SearchProviderFactory.KnownNames);
        }
    }
}
