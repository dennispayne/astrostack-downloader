using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Tests.Discovery;

/// <summary>
/// A deny-by-default fake <see cref="IHttpGateway"/>: every request must be explicitly registered via
/// <see cref="AddResponse"/>/<see cref="AddResponder"/>, and any unregistered host is recorded and
/// throws, so tests can assert "no unexpected network calls"
/// (docs/REQUIREMENTS.md, "Testing": "with a deny-all fake HTTP layer, assert the tool contacts only
/// vendor download hosts and configured providers").
/// </summary>
public sealed class FakeHttpGateway : IHttpGateway
{
    private readonly List<Func<HttpRequestSpec, FakeHttpResponse?>> _responders = new();
    private readonly List<Uri> _requestedUrls = new();

    public IReadOnlyList<Uri> RequestedUrls => _requestedUrls;

    /// <summary>Registers an exact-URL canned response.</summary>
    public FakeHttpGateway AddResponse(string url, FakeHttpResponse response)
    {
        var target = new Uri(url);
        _responders.Add(req => req.Url == target ? response : null);
        return this;
    }

    /// <summary>Registers a predicate-driven responder, checked in registration order.</summary>
    public FakeHttpGateway AddResponder(Func<HttpRequestSpec, FakeHttpResponse?> responder)
    {
        _responders.Add(responder);
        return this;
    }

    public Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
    {
        _requestedUrls.Add(request.Url);

        foreach (var responder in _responders)
        {
            var match = responder(request);
            if (match is not null)
            {
                return Task.FromResult(match.ToHttpResponseSpec(request.Url));
            }
        }

        throw new InvalidOperationException(
            $"FakeHttpGateway: unexpected network call to '{request.Url}'. Register a response or this indicates an unwanted network destination.");
    }
}

/// <summary>A canned response for <see cref="FakeHttpGateway"/>.</summary>
public sealed class FakeHttpResponse
{
    public int StatusCode { get; init; } = 200;

    public byte[] Body { get; init; } = [];

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Uri? RedirectTo { get; init; }

    public static FakeHttpResponse Redirect(string location, int status = 302) => new()
    {
        StatusCode = status,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Location"] = location },
    };

    public static FakeHttpResponse Html(string html, int status = 200) => new()
    {
        StatusCode = status,
        Body = System.Text.Encoding.UTF8.GetBytes(html),
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/html" },
    };

    public static FakeHttpResponse Json(string json, int status = 200) => new()
    {
        StatusCode = status,
        Body = System.Text.Encoding.UTF8.GetBytes(json),
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json" },
    };

    public static FakeHttpResponse Binary(byte[] body, string contentType = "application/octet-stream", int status = 200) => new()
    {
        StatusCode = status,
        Body = body,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = contentType,
            ["Content-Length"] = body.Length.ToString(),
        },
    };

    internal HttpResponseSpec ToHttpResponseSpec(Uri requestUrl) =>
        new(requestUrl, StatusCode, Headers, new MemoryStream(Body));
}
