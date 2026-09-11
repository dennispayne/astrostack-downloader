using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Verification;

namespace WgFetch.Core.Downloads;

public enum DownloadStatus
{
    Downloaded,
    AlreadyPresent,
    Resumed,
    VerificationFailed,
    HashMismatch,
    LengthMismatch,
    RequiresAuthentication,
    NetworkError,
    Cancelled,
}

/// <summary>Sidecar metadata for a partial download, used to decide whether a resume is safe.</summary>
public sealed record PartialMeta
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>ETag or Last-Modified captured from the first response.</summary>
    [JsonPropertyName("validator")]
    public string? Validator { get; init; }

    [JsonPropertyName("expectedLength")]
    public long? ExpectedLength { get; init; }

    [JsonPropertyName("bytesWritten")]
    public long BytesWritten { get; init; }

    [JsonPropertyName("upstreamSha256")]
    public string? UpstreamSha256 { get; init; }
}

public sealed record DownloadRequest
{
    public required Uri Url { get; init; }

    /// <summary>Absolute path the installer takes once — and only once — it is fully verified.</summary>
    public required string FinalPath { get; init; }

    public long? ExpectedLength { get; init; }

    public string? Validator { get; init; }

    public bool ServerAcceptsRanges { get; init; }

    /// <summary>Vendor-published hash, when one exists.</summary>
    public string? UpstreamSha256 { get; init; }

    public bool RequireHashMatch { get; init; }

    public bool NoResume { get; init; }
}

public sealed record DownloadResult
{
    public required DownloadStatus Status { get; init; }

    public required string Reason { get; init; }

    public string? Path { get; init; }

    public string? Sha256 { get; init; }

    public long BytesWritten { get; init; }

    public bool Resumed { get; init; }

    public bool? UpstreamHashMatched { get; init; }

    public InstallerFormat Format { get; init; } = InstallerFormat.Unknown;

    public bool Success => Status is DownloadStatus.Downloaded or DownloadStatus.AlreadyPresent or DownloadStatus.Resumed;

    public ExitCode ToExitCode() => Status switch
    {
        DownloadStatus.Downloaded or DownloadStatus.AlreadyPresent or DownloadStatus.Resumed => ExitCode.Success,
        DownloadStatus.HashMismatch => ExitCode.HashMismatch,
        DownloadStatus.RequiresAuthentication => ExitCode.RequiresAuth,
        DownloadStatus.NetworkError => ExitCode.NetworkError,
        DownloadStatus.Cancelled => ExitCode.Cancelled,
        _ => ExitCode.VerificationFailed,
    };
}

/// <summary>
/// Resumable, validator-guarded, atomically renamed downloads. The final path never exists in an
/// unverified state, and two builds are never spliced together
/// (docs/REQUIREMENTS.md, "Downloads — resumable and atomic").
/// </summary>
public sealed class InstallerDownloader
{
    public const string PartialDirectoryName = ".partial";

    private readonly IHttpGateway _http;
    private readonly ILogger _logger;

    public InstallerDownloader(IHttpGateway http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Removes partial downloads older than <paramref name="maxAge"/>.</summary>
    public int CleanStalePartials(string installerDirectory, TimeSpan maxAge, DateTimeOffset now)
    {
        var partialDir = Path.Combine(installerDirectory, PartialDirectoryName);
        if (!Directory.Exists(partialDir))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(partialDir))
        {
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            if (now - lastWrite <= maxAge)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
                _logger.LogDebug("Removed stale partial {File}.", file);
            }
            catch (IOException ex)
            {
                _logger.LogDebug("Could not remove stale partial {File}: {Message}", file, ex.Message);
            }
        }

        return removed;
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var finalPath = Path.GetFullPath(request.FinalPath);
        var installerDir = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("FinalPath must include a directory.", nameof(request));
        Directory.CreateDirectory(installerDir);

        if (File.Exists(finalPath) && request.UpstreamSha256 is { Length: > 0 } expected)
        {
            var existing = await ComputeSha256Async(finalPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{Path} already matches the expected hash; skipping download.", finalPath);
                return new DownloadResult
                {
                    Status = DownloadStatus.AlreadyPresent,
                    Reason = "file already present with matching SHA256",
                    Path = finalPath,
                    Sha256 = existing,
                    BytesWritten = new FileInfo(finalPath).Length,
                    UpstreamHashMatched = true,
                };
            }
        }

        var partialDir = Path.Combine(installerDir, PartialDirectoryName);
        Directory.CreateDirectory(partialDir);

        var (tempPath, metaPath, resumeFrom, resumed) = PrepareTemp(request, partialDir);

        try
        {
            var outcome = await TransferAsync(request, tempPath, metaPath, resumeFrom, resumed, cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.Success)
            {
                SafeDelete(tempPath);
                SafeDelete(metaPath);
                return outcome;
            }

            // The transfer may have abandoned the resume (a 200 answer to a ranged request forces a
            // restart from zero), so the transfer's own verdict wins over the pre-flight intent.
            resumed = outcome.Resumed;

            var bytes = new FileInfo(tempPath).Length;
            if (request.ExpectedLength is { } expectedLength && bytes != expectedLength)
            {
                SafeDelete(tempPath);
                SafeDelete(metaPath);
                return new DownloadResult
                {
                    Status = DownloadStatus.LengthMismatch,
                    Reason = $"downloaded {bytes} bytes but expected {expectedLength}",
                    BytesWritten = bytes,
                };
            }

            var sha = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            bool? hashMatched = null;
            if (request.UpstreamSha256 is { Length: > 0 } upstream)
            {
                hashMatched = string.Equals(sha, upstream, StringComparison.OrdinalIgnoreCase);
                if (hashMatched == false)
                {
                    _logger.LogError(
                        "SHA256 mismatch for {Url}: computed {Computed}, upstream published {Upstream}.",
                        Logging.SecretRedactor.RedactUrl(request.Url),
                        sha,
                        upstream);

                    if (request.RequireHashMatch)
                    {
                        SafeDelete(tempPath);
                        SafeDelete(metaPath);
                        return new DownloadResult
                        {
                            Status = DownloadStatus.HashMismatch,
                            Reason = $"computed SHA256 {sha} does not match upstream {upstream}",
                            Sha256 = sha,
                            BytesWritten = bytes,
                            UpstreamHashMatched = false,
                        };
                    }
                }
            }

            // Atomic publish: the final path only ever appears fully verified.
            File.Move(tempPath, finalPath, overwrite: true);
            SafeDelete(metaPath);

            _logger.LogInformation("Acquired {Path} ({Bytes} bytes, SHA256 {Sha}).", finalPath, bytes, sha);

            return new DownloadResult
            {
                Status = resumed ? DownloadStatus.Resumed : DownloadStatus.Downloaded,
                Reason = resumed ? "resumed and verified" : "downloaded and verified",
                Path = finalPath,
                Sha256 = sha,
                BytesWritten = bytes,
                Resumed = resumed,
                UpstreamHashMatched = hashMatched,
                Format = outcome.Format,
            };
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempPath);
            SafeDelete(metaPath);
            throw;
        }
    }

    private (string TempPath, string MetaPath, long ResumeFrom, bool Resumed) PrepareTemp(
        DownloadRequest request,
        string partialDir)
    {
        if (!request.NoResume && request.Validator is { Length: > 0 } && request.ServerAcceptsRanges)
        {
            foreach (var candidateMeta in Directory.EnumerateFiles(partialDir, "*.meta"))
            {
                PartialMeta? meta;
                try
                {
                    meta = JsonSerializer.Deserialize(File.ReadAllText(candidateMeta), DownloadJsonContext.Default.PartialMeta);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (meta is null ||
                    !string.Equals(meta.Url, request.Url.ToString(), StringComparison.Ordinal) ||
                    !string.Equals(meta.Validator, request.Validator, StringComparison.Ordinal) ||
                    meta.ExpectedLength != request.ExpectedLength)
                {
                    continue;
                }

                var partialData = candidateMeta[..^".meta".Length];
                if (!File.Exists(partialData))
                {
                    continue;
                }

                var written = new FileInfo(partialData).Length;
                if (written <= 0 || (request.ExpectedLength is { } total && written >= total))
                {
                    continue;
                }

                _logger.LogInformation("Resuming {Url} from byte {Offset}.", Logging.SecretRedactor.RedactUrl(request.Url), written);
                return (partialData, candidateMeta, written, true);
            }
        }

        var name = SanitizeName(Path.GetFileNameWithoutExtension(request.FinalPath));
        var temp = Path.Combine(partialDir, $"{name}-{Guid.NewGuid():N}.tmp");
        return (temp, temp + ".meta", 0, false);
    }

    private async Task<DownloadResult> TransferAsync(
        DownloadRequest request,
        string tempPath,
        string metaPath,
        long resumeFrom,
        bool resumed,
        CancellationToken cancellationToken)
    {
        HttpResponseSpec response;
        try
        {
            response = await _http.SendAsync(
                new HttpRequestSpec
                {
                    Url = request.Url,
                    Verb = HttpVerb.Get,
                    RangeFrom = resumeFrom > 0 ? resumeFrom : null,
                    IfRange = resumeFrom > 0 ? request.Validator : null,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadResult { Status = DownloadStatus.NetworkError, Reason = $"request failed: {ex.Message}" };
        }

        await using (response.ConfigureAwait(false))
        {
            if (response.StatusCode is 401 or 403 or 407)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.RequiresAuthentication,
                    Reason = $"HTTP {response.StatusCode}: requires authentication (P1, unsupported)",
                };
            }

            if (!response.IsSuccess)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.NetworkError,
                    Reason = $"HTTP {response.StatusCode}",
                };
            }

            var append = false;
            if (resumeFrom > 0)
            {
                if (response.StatusCode == 206)
                {
                    append = true;
                }
                else
                {
                    // The server ignored Range (or the validator changed) and is sending the whole body.
                    // Splicing two builds must be impossible: discard the partial and restart at zero.
                    _logger.LogWarning(
                        "Resume rejected for {Url}: server answered {Status} instead of 206; restarting from zero.",
                        Logging.SecretRedactor.RedactUrl(request.Url),
                        response.StatusCode);
                    resumed = false;
                }
            }

            var validator = response.Validator ?? request.Validator;

            await using var file = new FileStream(
                tempPath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);

            WriteMeta(metaPath, new PartialMeta
            {
                Url = request.Url.ToString(),
                Validator = validator,
                ExpectedLength = request.ExpectedLength ?? response.ResourceLength,
                BytesWritten = append ? resumeFrom : 0,
                UpstreamSha256 = request.UpstreamSha256,
            });

            var buffer = new byte[128 * 1024];
            long written = append ? resumeFrom : 0;
            var inspected = !append ? new List<byte>(MagicBytes.InspectionChunkSize) : null;
            var format = InstallerFormat.Unknown;

            while (true)
            {
                var read = await response.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (inspected is not null && inspected.Count < MagicBytes.InspectionChunkSize)
                {
                    var take = Math.Min(read, MagicBytes.InspectionChunkSize - inspected.Count);
                    inspected.AddRange(buffer.AsSpan(0, take).ToArray());

                    if (inspected.Count >= 512 || read < buffer.Length)
                    {
                        var chunk = inspected.ToArray();
                        if (MagicBytes.LooksLikeMarkup(chunk))
                        {
                            // Reject a login page at 4KB, not at 200MB.
                            return new DownloadResult
                            {
                                Status = DownloadStatus.VerificationFailed,
                                Reason = "payload begins with markup — login, error or interstitial page",
                            };
                        }

                        format = MagicBytes.Classify(chunk);
                        if (!MagicBytes.IsAcceptedInstaller(format))
                        {
                            return new DownloadResult
                            {
                                Status = DownloadStatus.VerificationFailed,
                                Reason = "leading bytes match no known installer format",
                            };
                        }

                        inspected = null;
                    }
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (inspected is { Count: > 0 })
            {
                var chunk = inspected.ToArray();
                if (MagicBytes.LooksLikeMarkup(chunk))
                {
                    return new DownloadResult
                    {
                        Status = DownloadStatus.VerificationFailed,
                        Reason = "payload begins with markup — login, error or interstitial page",
                    };
                }

                format = MagicBytes.Classify(chunk);
                if (!MagicBytes.IsAcceptedInstaller(format))
                {
                    return new DownloadResult
                    {
                        Status = DownloadStatus.VerificationFailed,
                        Reason = "leading bytes match no known installer format",
                    };
                }
            }

            WriteMeta(metaPath, new PartialMeta
            {
                Url = request.Url.ToString(),
                Validator = validator,
                ExpectedLength = request.ExpectedLength ?? response.ResourceLength,
                BytesWritten = written,
                UpstreamSha256 = request.UpstreamSha256,
            });

            return new DownloadResult
            {
                Status = resumed ? DownloadStatus.Resumed : DownloadStatus.Downloaded,
                Reason = "transfer complete",
                BytesWritten = written,
                Resumed = resumed,
                Format = format,
            };
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteMeta(string metaPath, PartialMeta meta) =>
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, DownloadJsonContext.Default.PartialMeta));

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: a locked partial is cleaned up by the stale-partial sweep on the next run.
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "installer" : cleaned;
    }
}

[JsonSerializable(typeof(PartialMeta))]
internal sealed partial class DownloadJsonContext : JsonSerializerContext;
