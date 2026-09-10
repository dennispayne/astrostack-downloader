namespace WgFetch.Core.Abstractions;

/// <summary>HTTP verbs used by wgfetch. No verb that mutates remote state is ever issued.</summary>
public enum HttpVerb
{
    Head,
    Get,
}

/// <summary>A single outbound HTTP request. Redirects are never followed automatically.</summary>
public sealed record HttpRequestSpec
{
    public required Uri Url { get; init; }

    public HttpVerb Verb { get; init; } = HttpVerb.Get;

    /// <summary>Inclusive first byte of a ranged request, or null for no <c>Range</c> header.</summary>
    public long? RangeFrom { get; init; }

    /// <summary>Inclusive last byte of a ranged request.</summary>
    public long? RangeTo { get; init; }

    /// <summary>Validator (ETag or Last-Modified) sent as <c>If-Range</c> when resuming.</summary>
    public string? IfRange { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A response with its body still unread, so callers can stream or discard it.</summary>
public sealed class HttpResponseSpec : IAsyncDisposable
{
    private readonly Func<ValueTask>? _dispose;

    public HttpResponseSpec(
        Uri requestUrl,
        int statusCode,
        IReadOnlyDictionary<string, string> headers,
        Stream body,
        Func<ValueTask>? dispose = null)
    {
        RequestUrl = requestUrl;
        StatusCode = statusCode;
        Headers = headers;
        Body = body;
        _dispose = dispose;
    }

    public Uri RequestUrl { get; }

    public int StatusCode { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public Stream Body { get; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public bool IsRedirect => StatusCode is 301 or 302 or 303 or 307 or 308;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? ContentType => Header("Content-Type");

    public long? ContentLength =>
        long.TryParse(Header("Content-Length"), out var length) ? length : null;

    /// <summary>Total length of the resource, taking <c>Content-Range</c> into account for partial responses.</summary>
    public long? ResourceLength
    {
        get
        {
            var contentRange = Header("Content-Range");
            if (!string.IsNullOrEmpty(contentRange))
            {
                var slash = contentRange.LastIndexOf('/');
                if (slash >= 0 && long.TryParse(contentRange[(slash + 1)..], out var total))
                {
                    return total;
                }
            }

            return ContentLength;
        }
    }

    public string? Validator => Header("ETag") ?? Header("Last-Modified");

    public bool AcceptsRanges =>
        string.Equals(Header("Accept-Ranges"), "bytes", StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync() => _dispose?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>
/// The single seam through which every network byte flows. Tests substitute a fake so the hermetic
/// suite can assert that no unexpected destination is ever contacted.
/// </summary>
public interface IHttpGateway
{
    Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken);
}
