using System.Text;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Tests.Support;

/// <summary>
/// Deterministic in-memory HTTP seam. Any request to a URL that was not explicitly registered throws,
/// so tests can assert that wgfetch contacts nothing it was not told to contact
/// (docs/REQUIREMENTS.md: "No unexpected network calls").
/// </summary>
public sealed class StubHttpGateway : IHttpGateway
{
    private readonly Dictionary<string, Func<HttpRequestSpec, StubResponse>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestSpec> Requests { get; } = [];

    public IReadOnlyList<string> ContactedHosts =>
        Requests.Select(r => r.Url.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public StubHttpGateway Map(string url, StubResponse response) => Map(url, _ => response);

    public StubHttpGateway Map(string url, Func<HttpRequestSpec, StubResponse> factory)
    {
        _routes[url] = factory;
        return this;
    }

    public Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);

        if (!_routes.TryGetValue(request.Url.ToString(), out var factory))
        {
            throw new InvalidOperationException($"Unexpected network call to {request.Url}");
        }

        var stub = factory(request);
        var headers = new Dictionary<string, string>(stub.Headers, StringComparer.OrdinalIgnoreCase);
        var body = stub.Body;

        if (request.RangeFrom is { } from && stub.StatusCode == 206 && from < body.Length)
        {
            var to = Math.Min(request.RangeTo ?? body.Length - 1, body.Length - 1);
            var slice = body[(int)from..(int)(to + 1)];
            headers["Content-Range"] = $"bytes {from}-{to}/{body.Length}";
            headers["Content-Length"] = slice.Length.ToString();
            return Task.FromResult(new HttpResponseSpec(request.Url, 206, headers, new MemoryStream(slice)));
        }

        if (!headers.ContainsKey("Content-Length") && stub.StatusCode is >= 200 and < 300)
        {
            headers["Content-Length"] = (stub.AdvertisedLength ?? body.Length).ToString();
        }

        return Task.FromResult(new HttpResponseSpec(request.Url, stub.StatusCode, headers, new MemoryStream(body)));
    }
}

public sealed record StubResponse
{
    public int StatusCode { get; init; } = 200;

    public byte[] Body { get; init; } = [];

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lets a test advertise a length that differs from the body it actually returns.</summary>
    public long? AdvertisedLength { get; init; }

    public static StubResponse Binary(byte[] body, string contentType = "application/octet-stream", int status = 200) =>
        new()
        {
            StatusCode = status,
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = contentType },
        };

    public static StubResponse Html(string html, int status = 200) =>
        new()
        {
            StatusCode = status,
            Body = Encoding.UTF8.GetBytes(html),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/html; charset=utf-8" },
        };

    public static StubResponse Redirect(string location, int status = 302) =>
        new()
        {
            StatusCode = status,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Location"] = location },
        };

    public static StubResponse Status(int status) => new() { StatusCode = status };
}

/// <summary>Synthetic installer payloads with valid magic bytes, padded to a plausible length.</summary>
public static class FakeInstaller
{
    public const int DefaultSize = 256 * 1024;

    public static byte[] PortableExecutable(int size = DefaultSize) => WithPrefix([0x4D, 0x5A, 0x90, 0x00], size);

    public static byte[] Msi(int size = DefaultSize) => WithPrefix([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], size);

    public static byte[] Zip(int size = DefaultSize) => WithPrefix([0x50, 0x4B, 0x03, 0x04], size);

    public static byte[] InnoSetup(int size = DefaultSize)
    {
        var bytes = WithPrefix([0x4D, 0x5A, 0x90, 0x00], size);
        Encoding.ASCII.GetBytes("Inno Setup Setup Data").CopyTo(bytes, 512);
        return bytes;
    }

    public static byte[] Nsis(int size = DefaultSize)
    {
        var bytes = WithPrefix([0x4D, 0x5A, 0x90, 0x00], size);
        Encoding.ASCII.GetBytes("NullsoftInst").CopyTo(bytes, 512);
        return bytes;
    }

    private static byte[] WithPrefix(byte[] prefix, int size)
    {
        var bytes = new byte[Math.Max(size, prefix.Length)];
        prefix.CopyTo(bytes, 0);
        for (var i = prefix.Length; i < bytes.Length; i++)
        {
            // Deterministic filler so hashes are stable across runs.
            bytes[i] = (byte)(i * 31 % 251);
        }

        return bytes;
    }
}

/// <summary>A disposable temporary directory for filesystem-touching tests.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wgfetch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }
}
