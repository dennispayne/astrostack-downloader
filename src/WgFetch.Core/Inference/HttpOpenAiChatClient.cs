using System.Net.Http.Headers;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Inference;

/// <summary>
/// Production <see cref="IOpenAiChatClient"/> built directly on <see cref="HttpClient"/> (not
/// <see cref="IHttpGateway"/>, which is GET/HEAD-only — see <see cref="IOpenAiChatClient"/>). Sends
/// the same truthful project User-Agent as <see cref="HttpGateway"/>.
/// </summary>
public sealed class HttpOpenAiChatClient : IOpenAiChatClient, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpOpenAiChatClient(HttpClient? client = null, TimeSpan? timeout = null)
    {
        if (client is null)
        {
            _client = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(2) };
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }

        if (_client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd($"wgfetch/1.0 (+{HttpGateway.ProjectUrl})");
        }
    }

    public async Task<string> PostChatCompletionAsync(
        Uri endpoint,
        string jsonBody,
        string? apiKeyHeaderValue,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(apiKeyHeaderValue))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyHeaderValue);
        }

        using var response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
