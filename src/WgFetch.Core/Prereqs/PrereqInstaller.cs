using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Downloads;

namespace WgFetch.Core.Prereqs;

public enum PrereqState
{
    Missing,
    Present,
    HashMismatch,
    /// <summary>Present on disk but the expected digest is not compiled into this build.</summary>
    Unpinned,
}

public sealed record PrereqAssetStatus(string RelativePath, string Path, PrereqState State, long? SizeBytes, string? Sha256);

public sealed record PrereqModelStatus(string ModelId, string DisplayName, string Directory, IReadOnlyList<PrereqAssetStatus> Assets)
{
    public bool Ready => Assets.Count > 0 && Assets.All(a => a.State == PrereqState.Present);
}

public sealed record PrereqInstallResult(bool Success, IReadOnlyList<string> Messages, IReadOnlyList<PrereqModelStatus> Models)
{
    public ExitCode ExitCode => Success ? ExitCode.Success : ExitCode.MissingPrerequisite;
}

/// <summary>Manifest written after a successful install, recording what was placed where.</summary>
public sealed record PrereqInstallManifest
{
    [JsonPropertyName("installedUtc")]
    public required DateTimeOffset InstalledUtc { get; init; }

    [JsonPropertyName("models")]
    public required IReadOnlyList<PrereqInstalledModel> Models { get; init; }
}

public sealed record PrereqInstalledModel
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("repository")]
    public required string Repository { get; init; }

    [JsonPropertyName("revision")]
    public required string Revision { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<PrereqInstalledFile> Files { get; init; }
}

public sealed record PrereqInstalledFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("bytes")] long Bytes);

/// <summary>
/// Downloads the pinned models and verifies each against a SHA256 digest compiled into the binary,
/// hard-failing on mismatch (docs/REQUIREMENTS.md, "Prerequisites — frictionless first run").
/// A model whose digest is not pinned in this build is refused: unverified weights are never installed.
/// </summary>
public sealed class PrereqInstaller
{
    private readonly IHttpGateway _http;
    private readonly ILogger _logger;

    public PrereqInstaller(IHttpGateway http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public static string ModelDirectory(string modelsRoot, PinnedModel model) =>
        Path.Combine(modelsRoot, model.Id);

    /// <summary>Reports presence, paths, sizes and verification state for every pinned model.</summary>
    public static async Task<IReadOnlyList<PrereqModelStatus>> StatusAsync(
        string modelsRoot,
        CancellationToken cancellationToken)
    {
        return await StatusAsync(modelsRoot, PinnedModels.All, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<PrereqModelStatus>> StatusAsync(
        string modelsRoot,
        IReadOnlyList<PinnedModel> models,
        CancellationToken cancellationToken)
    {
        var result = new List<PrereqModelStatus>();
        foreach (var model in models)
        {
            var directory = ModelDirectory(modelsRoot, model);
            var assets = new List<PrereqAssetStatus>();
            foreach (var asset in model.Assets)
            {
                var path = Path.Combine(directory, asset.RelativePath);
                if (!File.Exists(path))
                {
                    assets.Add(new PrereqAssetStatus(asset.RelativePath, path, PrereqState.Missing, null, null));
                    continue;
                }

                var size = new FileInfo(path).Length;
                var sha = await InstallerDownloader.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                var state = !asset.IsPinned
                    ? PrereqState.Unpinned
                    : string.Equals(sha, asset.Sha256, StringComparison.OrdinalIgnoreCase)
                        ? PrereqState.Present
                        : PrereqState.HashMismatch;
                assets.Add(new PrereqAssetStatus(asset.RelativePath, path, state, size, sha));
            }

            result.Add(new PrereqModelStatus(model.Id, model.DisplayName, directory, assets));
        }

        return result;
    }

    /// <summary>
    /// Hashing-free readiness probe for latency-sensitive, cosmetic paths such as the no-command
    /// landing view: every asset must be digest-pinned, exist, and — where a size is pinned — match it
    /// on disk. An unpinned asset or a model that declares no assets is reported as not ready, exactly
    /// as the installer refuses unverifiable weights.
    /// Full digest verification stays in <see cref="StatusAsync(string, CancellationToken)"/>, which
    /// <c>prereqs status</c> and <c>verify</c> use — a bare <c>wgfetch</c> must never spend seconds
    /// hashing model files. Unreadable trees report "not ready" rather than throwing.
    /// </summary>
    public static bool QuickReady(string modelsRoot, IReadOnlyList<PinnedModel> models)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            return false;
        }

        try
        {
            foreach (var model in models)
            {
                if (model.Assets.Count == 0)
                {
                    return false;
                }

                var directory = ModelDirectory(modelsRoot, model);
                foreach (var asset in model.Assets)
                {
                    if (!asset.IsPinned)
                    {
                        return false;
                    }

                    var info = new FileInfo(Path.Combine(directory, asset.RelativePath));
                    if (!info.Exists || (asset.SizeBytes is { } expected && info.Length != expected))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The exact command a user must run when a model is missing.</summary>
    public static string MissingModelMessage(PinnedModel model)
    {
        var files = string.Join(", ", model.Assets.Select(a => $"{a.RelativePath}{(a.SizeBytes is { } s ? $" ({s} bytes)" : string.Empty)}"));
        var command = model.IsLanguageModel ? "wgfetch prereqs install --include-llm" : "wgfetch prereqs install";
        return $"Model '{model.Id}' is missing. Expected files: {files}. Obtain it with: {command}";
    }

    public async Task<PrereqInstallResult> InstallAsync(
        string modelsRoot,
        bool includeLanguageModel,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var success = true;
        var installed = new List<PrereqInstalledModel>();

        foreach (var model in PinnedModels.All)
        {
            if (model.IsLanguageModel && !includeLanguageModel)
            {
                messages.Add($"Skipping {model.DisplayName} (pass --include-llm to fetch it).");
                continue;
            }

            var directory = ModelDirectory(modelsRoot, model);

            if (!model.FullyPinned)
            {
                success = false;
                messages.Add(
                    $"Refusing to install {model.DisplayName}: this build has no pinned SHA256 for " +
                    $"{string.Join(", ", model.Assets.Where(a => !a.IsPinned).Select(a => a.RelativePath))}. " +
                    "Unverified model weights are never installed — see 'Known conflicts' in docs/REQUIREMENTS.md.");
                continue;
            }

            if (dryRun)
            {
                messages.Add($"Would install {model.DisplayName} ({model.Assets.Count} files) into {directory}.");
                continue;
            }

            Directory.CreateDirectory(directory);
            var files = new List<PrereqInstalledFile>();

            foreach (var asset in model.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, asset.RelativePath);
                if (File.Exists(path))
                {
                    var existing = await InstallerDownloader.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(existing, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(new PrereqInstalledFile(asset.RelativePath, existing, new FileInfo(path).Length));
                        continue;
                    }
                }

                var downloaded = await DownloadVerifiedAsync(asset, path, cancellationToken).ConfigureAwait(false);
                if (downloaded is null)
                {
                    success = false;
                    messages.Add($"Failed to install {asset.RelativePath} for {model.Id}; hard-failing on verification.");
                    break;
                }

                files.Add(downloaded);
                messages.Add($"Installed {asset.RelativePath} for {model.Id}.");
            }

            if (files.Count == model.Assets.Count)
            {
                installed.Add(new PrereqInstalledModel
                {
                    Id = model.Id,
                    Repository = model.Repository,
                    Revision = model.Revision,
                    Files = files,
                });
            }
        }

        if (!dryRun && installed.Count > 0)
        {
            var manifest = new PrereqInstallManifest
            {
                InstalledUtc = DateTimeOffset.UtcNow,
                Models = installed,
            };

            Directory.CreateDirectory(modelsRoot);
            await File.WriteAllTextAsync(
                Path.Combine(modelsRoot, "install-manifest.json"),
                JsonSerializer.Serialize(manifest, PrereqJsonContext.Default.PrereqInstallManifest),
                cancellationToken).ConfigureAwait(false);
        }

        var status = await StatusAsync(modelsRoot, cancellationToken).ConfigureAwait(false);
        return new PrereqInstallResult(success, messages, status);
    }

    private async Task<PrereqInstalledFile?> DownloadVerifiedAsync(
        ModelAsset asset,
        string path,
        CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        try
        {
            var response = await _http
                .SendAsync(new HttpRequestSpec { Url = new Uri(asset.Url), Verb = HttpVerb.Get }, cancellationToken)
                .ConfigureAwait(false);

            await using (response.ConfigureAwait(false))
            {
                if (!response.IsSuccess)
                {
                    _logger.LogError("Model download failed with HTTP {Status} for {Asset}.", response.StatusCode, asset.RelativePath);
                    return null;
                }

                await using var file = File.Create(temp);
                await response.Body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var sha = await InstallerDownloader.ComputeSha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Pinned hash mismatch for {Asset}: expected {Expected}, computed {Computed}.",
                    asset.RelativePath,
                    asset.Sha256,
                    sha);
                return null;
            }

            var bytes = new FileInfo(temp).Length;
            File.Move(temp, path, overwrite: true);
            return new PrereqInstalledFile(asset.RelativePath, sha, bytes);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PrereqInstallManifest))]
public sealed partial class PrereqJsonContext : JsonSerializerContext;
