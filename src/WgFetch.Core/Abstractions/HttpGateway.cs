using System.Net;
using System.Net.Http.Headers;

namespace WgFetch.Core.Abstractions;

/// <summary>
/// The production <see cref="IHttpGateway"/>. Automatic redirect handling is disabled so the
/// verification gate can enforce the allowlist on every hop, and a truthful User-Agent identifying
/// the project is always sent (docs/REQUIREMENTS.md, "Web search").
/// </summary>
public sealed class HttpGateway : IHttpGateway, IDisposable
{
    public const string ProjectUrl = "https://github.com/dennispayne/astrostack-downloader";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpGateway(HttpClient? client = null, TimeSpan? timeout = null)
    {
        if (client is null)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };

            _client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }

        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd($"wgfetch/1.0 (+{ProjectUrl})");
        }
    }

    public async Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            request.Verb == HttpVerb.Head ? HttpMethod.Head : HttpMethod.Get,
            request.Url);

        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (request.RangeFrom is { } from)
        {
            message.Headers.Range = new RangeHeaderValue(from, request.RangeTo);
        }

        if (!string.IsNullOrEmpty(request.IfRange))
        {
            message.Headers.TryAddWithoutValidation("If-Range", request.IfRange);
        }

        var response = await _client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseSpec(
            request.Url,
            (int)response.StatusCode,
            headers,
            body,
            () =>
            {
                response.Dispose();
                return ValueTask.CompletedTask;
            });
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
