using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Logging;

namespace WgFetch.Core.Search;

/// <summary>
/// Shared plumbing for search providers that call a JSON/XML API over <see cref="IHttpGateway"/>:
/// robots.txt consultation, per-host rate limiting, the truthful project User-Agent (added by
/// <see cref="HttpGateway"/> itself) and secret-safe error messages. Concrete providers only need to
/// build the request URI/headers and parse the response body — this keeps "adding a provider" to
/// implementing <see cref="ISearchProvider"/> and nothing else.
/// </summary>
public abstract class HttpSearchProviderBase : ISearchProvider
{
    private readonly IHttpGateway _http;
    private readonly RobotsPolicy _robots;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger _logger;

    protected HttpSearchProviderBase(
        IHttpGateway http,
        RobotsPolicy? robots = null,
        HostRateLimiter? rateLimiter = null,
        ILogger? logger = null)
    {
        _http = http;
        _robots = robots ?? new RobotsPolicy(http);
        _rateLimiter = rateLimiter ?? new HostRateLimiter();
        _logger = logger ?? NullLogger.Instance;
    }

    public abstract string Name { get; }

    /// <summary>Builds the API request for a query. Never returns a URL to the engine's HTML result page.</summary>
    protected abstract HttpRequestSpec BuildRequest(string query, int maxResults);

    /// <summary>Parses the provider's response body into ranked results.</summary>
    protected abstract IReadOnlyList<SearchResult> ParseResults(string body, int maxResults);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var request = BuildRequest(query, maxResults);

        if (!await _robots.IsAllowedAsync(request.Url, HttpGateway.ProjectUrl, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Search provider '{Provider}': robots.txt disallows {Url}; skipping.", Name, SecretRedactor.RedactUrl(request.Url));
            return Array.Empty<SearchResult>();
        }

        await _rateLimiter.WaitAsync(request.Url.Host, cancellationToken).ConfigureAwait(false);

        HttpResponseSpec response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Search provider '{Provider}' request failed: {Message}", Name, SecretRedactor.Redact(ex.Message));
            return Array.Empty<SearchResult>();
        }

        await using (response.ConfigureAwait(false))
        {
            if (!response.IsSuccess)
            {
                _logger.LogWarning("Search provider '{Provider}' returned HTTP {Status}.", Name, response.StatusCode);
                return Array.Empty<SearchResult>();
            }

            using var reader = new StreamReader(response.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return ParseResults(body, maxResults);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Search provider '{Provider}' returned unparsable results: {Message}", Name, ex.Message);
                return Array.Empty<SearchResult>();
            }
        }
    }
}
