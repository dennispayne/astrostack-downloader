using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;

namespace WgFetch.Core.Verification;

/// <summary>Why a candidate URL was rejected, or that it was accepted.</summary>
public enum VerificationStatus
{
    Accepted,
    NotHttps,
    HostNotAllowlisted,
    RedirectOffAllowlist,
    TooManyRedirects,
    RequestFailed,
    RequiresAuthentication,
    NotBinaryContentType,
    MarkupPayload,
    ImplausibleLength,
    UnknownInstallerFormat,
    Cancelled,
}

/// <summary>Result of running a candidate URL through the mechanical verification gate.</summary>
public sealed record VerificationResult
{
    public required VerificationStatus Status { get; init; }

    public required Uri CandidateUrl { get; init; }

    /// <summary>The URL after following allowlisted redirects; equal to the candidate when none occurred.</summary>
    public Uri? FinalUrl { get; init; }

    public string? ContentType { get; init; }

    public long? ContentLength { get; init; }

    public InstallerFormat Format { get; init; } = InstallerFormat.Unknown;

    public string? Validator { get; init; }

    public bool AcceptsRanges { get; init; }

    public IReadOnlyList<Uri> RedirectChain { get; init; } = Array.Empty<Uri>();

    public required string Reason { get; init; }

    public bool Accepted => Status == VerificationStatus.Accepted;

    public ExitCode ToExitCode() => Status switch
    {
        VerificationStatus.Accepted => ExitCode.Success,
        VerificationStatus.RequiresAuthentication => ExitCode.RequiresAuth,
        VerificationStatus.Cancelled => ExitCode.Cancelled,
        VerificationStatus.RequestFailed => ExitCode.NetworkError,
        _ => ExitCode.VerificationFailed,
    };
}

/// <summary>Tunable bounds for the "plausible length" check.</summary>
public sealed record VerificationOptions
{
    /// <summary>Smaller than this and the payload is far more likely an error page than an installer.</summary>
    public long MinimumInstallerBytes { get; init; } = 32 * 1024;

    public long MaximumInstallerBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    public int MaximumRedirects { get; init; } = 5;
}

/// <summary>A response reached by following only HTTPS redirects within an allowlist.</summary>
public sealed class RedirectFollowResult : IAsyncDisposable
{
    internal RedirectFollowResult(
        Uri candidateUrl,
        Uri? finalUrl,
        IReadOnlyList<Uri> redirectChain,
        HttpResponseSpec? response,
        VerificationStatus? failureStatus,
        string? failureReason)
    {
        CandidateUrl = candidateUrl;
        FinalUrl = finalUrl;
        RedirectChain = redirectChain;
        Response = response;
        FailureStatus = failureStatus;
        FailureReason = failureReason;
    }

    public Uri CandidateUrl { get; }

    public Uri? FinalUrl { get; }

    public IReadOnlyList<Uri> RedirectChain { get; }

    /// <summary>The final non-redirect response; callers must dispose this result when finished with it.</summary>
    public HttpResponseSpec? Response { get; }

    /// <summary>
    /// The redirect-check failure, when any. Only <see cref="VerificationStatus.NotHttps"/>,
    /// <see cref="VerificationStatus.HostNotAllowlisted"/>, <see cref="VerificationStatus.RedirectOffAllowlist"/>,
    /// <see cref="VerificationStatus.TooManyRedirects"/>, and <see cref="VerificationStatus.RequestFailed"/>
    /// are returned by this redirect-only operation.
    /// </summary>
    public VerificationStatus? FailureStatus { get; }

    public string? FailureReason { get; }

    public bool Succeeded => Response is not null;

    public ValueTask DisposeAsync() => Response?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// The mechanical accept/reject decision for a candidate installer URL. No model output can move a
/// URL past this gate; every check is deterministic and every rejection is logged with its reason
/// (docs/REQUIREMENTS.md, "The central safety invariant").
/// </summary>
public sealed class VerificationGate
{
    private static readonly string[] BinaryContentTypes =
    [
        "application/octet-stream",
        "application/x-msdownload",
        "application/x-msdos-program",
        "application/x-msi",
        "application/x-ms-installer",
        "application/x-msinstaller",
        "application/vnd.microsoft.portable-executable",
        "application/exe",
        "application/x-exe",
        "application/zip",
        "application/x-zip-compressed",
        "application/x-7z-compressed",
        "application/x-compressed",
        "application/x-download",
        "binary/octet-stream",
        "application/x-binary",
        "application/vnd.ms-cab-compressed",
        "application/msix",
        "application/vnd.ms-appx",
    ];

    private static readonly string[] MarkupContentTypes =
    [
        "text/", "application/xhtml", "application/json", "application/xml", "application/javascript",
    ];

    private readonly IHttpGateway _http;
    private readonly VerificationOptions _options;
    private readonly ILogger _logger;

    public VerificationGate(IHttpGateway http, VerificationOptions? options = null, ILogger? logger = null)
    {
        _http = http;
        _options = options ?? new VerificationOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Runs every gate check against a candidate URL, retaining no bytes on rejection.</summary>
    public async Task<VerificationResult> VerifyAsync(
        Uri candidate,
        DomainAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(allowlist);

        await using var redirected = await FollowAllowedRedirectsAsync(
            new HttpRequestSpec
            {
                Url = candidate,
                Verb = HttpVerb.Get,
                RangeFrom = 0,
                RangeTo = MagicBytes.InspectionChunkSize - 1,
            },
            allowlist,
            cancellationToken).ConfigureAwait(false);

        if (!redirected.Succeeded)
        {
            return Reject(
                candidate,
                redirected.FailureStatus ?? VerificationStatus.RequestFailed,
                redirected.FailureReason ?? "redirect processing failed",
                redirected.RedirectChain,
                redirected.FinalUrl);
        }

        var response = redirected.Response!;
        var current = redirected.FinalUrl!;
        var redirects = redirected.RedirectChain;

        if (response.StatusCode is 401 or 403 or 407)
        {
            return Reject(
                candidate,
                VerificationStatus.RequiresAuthentication,
                $"HTTP {response.StatusCode}: requires authentication (P1, unsupported)",
                redirects,
                current);
        }

        if (!response.IsSuccess)
        {
            return Reject(candidate, VerificationStatus.RequestFailed, $"HTTP {response.StatusCode}", redirects, current);
        }

        var contentType = response.ContentType;
        var length = response.ResourceLength;

        if (IsMarkupContentType(contentType))
        {
            return Reject(
                candidate,
                VerificationStatus.NotBinaryContentType,
                $"content-type '{contentType}' is not a binary payload",
                redirects,
                current,
                contentType,
                length);
        }

        if (contentType is not null && !IsBinaryContentType(contentType))
        {
            _logger.LogDebug(
                "Content-type '{ContentType}' for {Url} is unrecognised; deferring to magic bytes.",
                contentType,
                current);
        }

        var chunk = await ReadChunkAsync(response.Body, MagicBytes.InspectionChunkSize, cancellationToken)
            .ConfigureAwait(false);

        if (MagicBytes.LooksLikeMarkup(chunk))
        {
            return Reject(
                candidate,
                VerificationStatus.MarkupPayload,
                "payload begins with markup — login, error or interstitial page",
                redirects,
                current,
                contentType,
                length);
        }

        var format = MagicBytes.Classify(chunk);
        if (!MagicBytes.IsAcceptedInstaller(format))
        {
            return Reject(
                candidate,
                VerificationStatus.UnknownInstallerFormat,
                "leading bytes match no known installer format",
                redirects,
                current,
                contentType,
                length);
        }

        if (length is null)
        {
            _logger.LogDebug("No content length advertised for {Url}; length plausibility deferred to download.", current);
        }
        else if (length < _options.MinimumInstallerBytes || length > _options.MaximumInstallerBytes)
        {
            return Reject(
                candidate,
                VerificationStatus.ImplausibleLength,
                $"content length {length} bytes is implausible for an installer",
                redirects,
                current,
                contentType,
                length,
                format);
        }

        _logger.LogInformation(
            "Verification accepted {Url} ({Format}, {Length} bytes, content-type '{ContentType}').",
            current,
            format,
            length,
            contentType);

        return new VerificationResult
        {
            Status = VerificationStatus.Accepted,
            CandidateUrl = candidate,
            FinalUrl = current,
            ContentType = contentType,
            ContentLength = length,
            Format = format,
            Validator = response.Validator,
            AcceptsRanges = response.AcceptsRanges || response.StatusCode == 206,
            RedirectChain = redirects,
            Reason = $"accepted: {format}, {length?.ToString() ?? "unknown"} bytes",
        };
    }

    /// <summary>
    /// Sends a request while following only HTTPS redirects within <paramref name="allowlist"/>.
    /// The final response is returned unread so callers can apply payload-specific verification and must
    /// dispose the returned result after consuming it.
    /// </summary>
    public async Task<RedirectFollowResult> FollowAllowedRedirectsAsync(
        HttpRequestSpec request,
        DomainAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);

        var candidate = request.Url;
        if (!candidate.IsAbsoluteUri || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(candidate, null, [], VerificationStatus.NotHttps, $"scheme '{candidate.Scheme}' is not https");
        }

        if (!allowlist.Allows(candidate))
        {
            return Failed(
                candidate,
                null,
                [],
                VerificationStatus.HostNotAllowlisted,
                $"host '{candidate.Host}' is not on the allowlist [{string.Join(", ", allowlist.Domains)}]");
        }

        var redirects = new List<Uri>();
        var current = candidate;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseSpec response;
            try
            {
                response = await _http.SendAsync(request with { Url = current }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(
                    candidate,
                    current,
                    redirects,
                    VerificationStatus.RequestFailed,
                    $"request to '{Redact(current)}' failed: {Logging.SecretRedactor.Redact(ex.Message)}");
            }

            if (!response.IsRedirect)
            {
                return new RedirectFollowResult(candidate, current, redirects, response, null, null);
            }

            await using (response.ConfigureAwait(false))
            {
                var location = response.Header("Location");
                if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(current, location, out var next))
                {
                    return Failed(
                        candidate,
                        current,
                        redirects,
                        VerificationStatus.RequestFailed,
                        $"redirect from '{Redact(current)}' had no usable Location header");
                }

                if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return Failed(candidate, current, redirects, VerificationStatus.NotHttps, $"redirect to non-https URL '{Redact(next)}'");
                }

                if (!allowlist.Allows(next))
                {
                    return Failed(candidate, current, redirects, VerificationStatus.RedirectOffAllowlist, $"redirect to off-allowlist host '{next.Host}'");
                }

                if (redirects.Count >= _options.MaximumRedirects)
                {
                    return Failed(
                        candidate,
                        current,
                        redirects,
                        VerificationStatus.TooManyRedirects,
                        $"exceeded {_options.MaximumRedirects} redirects");
                }

                redirects.Add(next);
                if (!string.Equals(current.Authority, next.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    request = request with { Headers = RemoveCredentialHeaders(request.Headers) };
                }

                current = next;
            }
        }
    }

    internal static bool IsBinaryContentType(string contentType)
    {
        var value = Normalize(contentType);
        foreach (var accepted in BinaryContentTypes)
        {
            if (value.Equals(accepted, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsMarkupContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var value = Normalize(contentType);
        foreach (var markup in MarkupContentTypes)
        {
            if (value.StartsWith(markup, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string contentType)
    {
        var value = contentType.Trim().ToLowerInvariant();
        var semicolon = value.IndexOf(';');
        return semicolon >= 0 ? value[..semicolon].Trim() : value;
    }

    private static async Task<byte[]> ReadChunkAsync(Stream body, int size, CancellationToken cancellationToken)
    {
        var buffer = new byte[size];
        var read = 0;
        while (read < size)
        {
            var n = await body.ReadAsync(buffer.AsMemory(read, size - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == size ? buffer : buffer[..read];
    }

    private VerificationResult Reject(
        Uri candidate,
        VerificationStatus status,
        string reason,
        IReadOnlyList<Uri>? redirects = null,
        Uri? finalUrl = null,
        string? contentType = null,
        long? length = null,
        InstallerFormat format = InstallerFormat.Unknown)
    {
        _logger.LogWarning("Verification rejected {Url}: {Reason}", Redact(candidate), reason);
        return new VerificationResult
        {
            Status = status,
            CandidateUrl = candidate,
            FinalUrl = finalUrl,
            ContentType = contentType,
            ContentLength = length,
            Format = format,
            RedirectChain = redirects ?? Array.Empty<Uri>(),
            Reason = reason,
        };
    }

    private static string Redact(Uri uri) => Logging.SecretRedactor.RedactUrl(uri);

    private static IReadOnlyDictionary<string, string> RemoveCredentialHeaders(IReadOnlyDictionary<string, string> headers) =>
        headers
            .Where(header => !Logging.SecretRedactor.IsSensitiveHeaderName(header.Key))
            .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

    private static RedirectFollowResult Failed(
        Uri candidate,
        Uri? finalUrl,
        IReadOnlyList<Uri> redirects,
        VerificationStatus status,
        string reason) =>
        new(candidate, finalUrl, redirects, null, status, reason);
}
