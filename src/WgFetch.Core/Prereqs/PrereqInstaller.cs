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
    private readonly IReadOnlyList<PinnedModel> _models;

    /// <summary>
    /// One lock per manifest path, serializing each install from asset writes through the manifest
    /// replacement. The in-process semaphore is paired with an OS-backed file lock so separate
    /// wgfetch processes cannot race either. Entries are removed once no local install is in flight.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RefCountedLock> ManifestLocks = new(StringComparer.Ordinal);

    private sealed class RefCountedLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int RefCount;
    }

    private static readonly TimeSpan ManifestLockRetryDelay = TimeSpan.FromMilliseconds(25);

    public PrereqInstaller(IHttpGateway http, ILogger? logger = null, IReadOnlyList<PinnedModel>? models = null)
    {
        _http = http;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _models = models ?? PinnedModels.All;
    }

    internal static async Task<IDisposable> AcquireManifestLockAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var key = NormalizeManifestKey(manifestPath);
        RefCountedLock entry;
        lock (ManifestLocks)
        {
            entry = ManifestLocks.AddOrUpdate(
                key,
                static _ => new RefCountedLock { RefCount = 1 },
                static (_, existing) => { existing.RefCount++; return existing; });
        }

        var lockAcquired = false;
        FileStream? fileLock = null;
        try
        {
            var wait = entry.Semaphore.WaitAsync(cancellationToken);
            await wait.ConfigureAwait(false);
            lockAcquired = true;
            fileLock = await AcquireManifestFileLockAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            return new ManifestLockScope(key, entry, fileLock);
        }
        catch
        {
            fileLock?.Dispose();
            ReleaseManifestLockReference(key, entry, releaseSemaphore: lockAcquired);
            throw;
        }
    }

    internal static bool IsManifestLockTracked(string manifestPath) =>
        ManifestLocks.ContainsKey(NormalizeManifestKey(manifestPath));

    internal static int GetManifestLockReferenceCount(string manifestPath) =>
        ManifestLocks.TryGetValue(NormalizeManifestKey(manifestPath), out var entry)
            ? Volatile.Read(ref entry.RefCount)
            : 0;

    /// <summary>
    /// Releases an acquired semaphore permit when <paramref name="releaseSemaphore"/> is true, then
    /// releases the caller's reference. Canceled waiters pass false because they never acquired a
    /// permit; the final reference disposes the semaphore only after all holders and waiters are gone.
    /// </summary>
    private static void ReleaseManifestLockReference(string key, RefCountedLock entry, bool releaseSemaphore)
    {
        lock (ManifestLocks)
        {
            if (releaseSemaphore)
            {
                entry.Semaphore.Release();
            }

            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                ManifestLocks.TryRemove(key, out _);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class ManifestLockScope(string key, RefCountedLock entry, FileStream fileLock) : IDisposable
    {
        public void Dispose()
        {
            fileLock.Dispose();
            ReleaseManifestLockReference(key, entry, releaseSemaphore: true);
        }
    }

    public static string ModelDirectory(string modelsRoot, PinnedModel model) =>
        ResolveConfinedPath(modelsRoot, model.Id, $"model id '{model.Id}'");

    /// <summary>
    /// Combines <paramref name="root"/> with <paramref name="relativeSegment"/> and verifies the
    /// normalized result stays under <paramref name="root"/>. Pinned model data can come from a
    /// caller-supplied list (see the <see cref="StatusAsync(string, IReadOnlyList{PinnedModel}?, CancellationToken)"/>
    /// and <see cref="InstallAsync(string, IReadOnlySet{string}, bool, CancellationToken)"/> overloads),
    /// so a rooted or `..`-bearing id/relative path is rejected before any filesystem operation.
    /// </summary>
    private static string ResolveConfinedPath(string root, string relativeSegment, string description)
    {
        if (string.IsNullOrEmpty(relativeSegment) || Path.IsPathRooted(relativeSegment))
        {
            throw new InvalidOperationException($"Refusing to use {description}: paths must be relative.");
        }

        var normalizedRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativeSegment));
        var relativeToRoot = Path.GetRelativePath(normalizedRoot, combined);
        if (relativeToRoot == ".." || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relativeToRoot))
        {
            throw new InvalidOperationException($"Refusing to use {description}: it escapes the models root.");
        }

        return combined;
    }

    private static void RejectReparsePointComponents(string root, string target, string description)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedTarget = Path.GetFullPath(target);
        var relativeToRoot = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        if (relativeToRoot == ".")
        {
            return;
        }

        if (relativeToRoot == ".." ||
            relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativeToRoot.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativeToRoot))
        {
            throw new InvalidOperationException($"Refusing to use {description}: it escapes the models root.");
        }

        var current = normalizedRoot;
        foreach (var part in relativeToRoot.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Refusing to use {description}: '{current}' is a symbolic link or reparse point.");
            }
        }
    }

    private static void RejectReparsePointRoot(string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        try
        {
            if ((File.GetAttributes(normalizedRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Refusing to use models root '{normalizedRoot}': it is a symbolic link or reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
            // A not-yet-existing root cannot itself be a reparse point; child paths are checked later.
        }
        catch (DirectoryNotFoundException)
        {
            // A not-yet-existing root cannot itself be a reparse point; child paths are checked later.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Unable to verify models root '{normalizedRoot}' before writing prerequisites.", exception);
        }
    }

    /// <summary>Reports presence, paths, sizes and verification state for every pinned model.</summary>
    public static Task<IReadOnlyList<PrereqModelStatus>> StatusAsync(
        string modelsRoot,
        CancellationToken cancellationToken) =>
        StatusAsync(modelsRoot, null, cancellationToken);

    /// <summary>Reports presence, paths, sizes and verification state for the supplied pinned models.</summary>
    public static async Task<IReadOnlyList<PrereqModelStatus>> StatusAsync(
        string modelsRoot,
        IReadOnlyList<PinnedModel>? models,
        CancellationToken cancellationToken)
    {
        RejectReparsePointRoot(modelsRoot);
        var result = new List<PrereqModelStatus>();
        foreach (var model in models ?? PinnedModels.All)
        {
            var directory = ModelDirectory(modelsRoot, model);
            RejectReparsePointComponents(modelsRoot, directory, $"model directory for '{model.Id}'");
            var assets = new List<PrereqAssetStatus>();
            foreach (var asset in model.Assets)
            {
                var path = ResolveConfinedPath(directory, asset.RelativePath, $"asset relative path '{asset.RelativePath}'");
                RejectReparsePointComponents(modelsRoot, path, $"asset path '{asset.RelativePath}' for '{model.Id}'");
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
        IReadOnlySet<string> selectedModels = _models
            .Where(model => includeLanguageModel || !model.IsLanguageModel)
            .Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        var result = await InstallAsync(modelsRoot, selectedModels, dryRun, cancellationToken).ConfigureAwait(false);
        return includeLanguageModel
            ? result
            : result with { Messages = [.. result.Messages, $"Skipping {PinnedModels.LanguageModel.DisplayName} (pass --include-llm to fetch it)."] };
    }

    /// <summary>Installs only the selected pinned models, retaining the same digest verification gate.</summary>
    public async Task<PrereqInstallResult> InstallAsync(
        string modelsRoot,
        IReadOnlySet<string> selectedModels,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        RejectReparsePointRoot(modelsRoot);
        var messages = new List<string>();
        var success = true;
        var installed = new List<PrereqInstalledModel>();
        var manifestPath = Path.Combine(modelsRoot, "install-manifest.json");
        var hasInstallWork = !dryRun && _models.Any(model => selectedModels.Contains(model.Id) && model.FullyPinned);
        using var manifestLock = hasInstallWork
            ? await AcquireManifestLockAsync(manifestPath, cancellationToken).ConfigureAwait(false)
            : null;

        foreach (var model in _models)
        {
            if (!selectedModels.Contains(model.Id))
            {
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
            RejectReparsePointComponents(modelsRoot, directory, $"model directory for '{model.Id}'");
            var files = new List<PrereqInstalledFile>();

            foreach (var asset in model.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveConfinedPath(directory, asset.RelativePath, $"asset relative path '{asset.RelativePath}'");
                var assetDirectory = Path.GetDirectoryName(path)!;
                RejectReparsePointComponents(modelsRoot, assetDirectory, $"asset parent directory for '{asset.RelativePath}' in '{model.Id}'");
                Directory.CreateDirectory(assetDirectory);
                RejectReparsePointComponents(modelsRoot, path, $"asset path '{asset.RelativePath}' for '{model.Id}'");
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
            var existing = await LoadExistingManifestModelsAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var merged = existing
                .Where(model => installed.All(newModel => !string.Equals(newModel.Id, model.Id, StringComparison.Ordinal)))
                .Concat(installed)
                .ToArray();

            var manifest = new PrereqInstallManifest
            {
                InstalledUtc = DateTimeOffset.UtcNow,
                Models = merged,
            };

            Directory.CreateDirectory(modelsRoot);
            var temp = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temp,
                    JsonSerializer.Serialize(manifest, PrereqJsonContext.Default.PrereqInstallManifest),
                    cancellationToken).ConfigureAwait(false);
                File.Move(temp, manifestPath, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        var status = await StatusAsync(modelsRoot, _models, cancellationToken).ConfigureAwait(false);
        return new PrereqInstallResult(success, messages, status);
    }

    private static string NormalizeManifestKey(string manifestPath)
    {
        var full = Path.GetFullPath(manifestPath);
        // The lock only needs to serialize access to the same on-disk file: fold case solely where the
        // filesystem itself is case-insensitive (Windows/macOS), never on case-sensitive Linux paths.
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
    }

    private static async Task<FileStream> AcquireManifestFileLockAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var lockPath = fullPath + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception) when (FileLockContention.IsContention(exception))
            {
                await Task.Delay(ManifestLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Loads previously installed model entries from the manifest, so a selective install never drops
    /// the record of models installed in an earlier run. A missing or malformed manifest yields none,
    /// and any null entry (a manifest can be arbitrary JSON, e.g. <c>{"models":[null]}</c>) is dropped
    /// rather than propagated, so a later merge never dereferences a null model. Only malformed JSON is
    /// tolerated as an empty manifest: a transient <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> reading an existing manifest propagates instead, so the
    /// subsequent merge-and-write never overwrites a manifest we failed to read, which would otherwise
    /// silently drop every previously recorded model.
    /// </summary>
    private static async Task<IReadOnlyList<PrereqInstalledModel>> LoadExistingManifestModelsAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            // File.Exists also reports false for an existing but unreadable manifest, so open it
            // directly and treat only a genuinely missing file as an empty manifest.
            stream = File.OpenRead(manifestPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var manifest = await JsonSerializer
                    .DeserializeAsync(stream, PrereqJsonContext.Default.PrereqInstallManifest, cancellationToken)
                    .ConfigureAwait(false);
                return manifest?.Models?.Where(model => model is not null).ToArray() ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private async Task<PrereqInstalledFile?> DownloadVerifiedAsync(
        ModelAsset asset,
        string path,
        CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var currentUrl))
            {
                _logger.LogError("Model asset URL is not a valid absolute URI for {Asset}.", asset.RelativePath);
                return null;
            }

            HttpResponseSpec? response = null;
            var redirectCount = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The model list is caller-supplied, so the very first URL is validated here too: no
                // request may leave the process for a plaintext or off-allowlist host, not even the
                // pinned candidate itself (docs/REQUIREMENTS.md, "Safety invariant").
                if (!string.Equals(currentUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !IsAllowedDownloadHost(currentUrl.Host))
                {
                    _logger.LogError(
                        "Model download for {Asset} targeted a non-allowlisted destination '{Scheme}://{Host}'; " +
                        "only HTTPS requests to an allowlisted download host are permitted.",
                        asset.RelativePath,
                        currentUrl.Scheme,
                        currentUrl.Host);
                    return null;
                }

                response = await _http
                    .SendAsync(new HttpRequestSpec { Url = currentUrl, Verb = HttpVerb.Get }, cancellationToken)
                    .ConfigureAwait(false);

                // Hugging Face's own host issues the metadata redirect, then hands large (LFS/Xet-backed)
                // blobs off to a CDN host; HttpGateway disables automatic redirects so the allowlist gate
                // can decide every hop explicitly (docs/REQUIREMENTS.md, model pinning).
                if (!response.IsRedirect)
                {
                    break;
                }

                var location = response.Header("Location");
                await response.DisposeAsync().ConfigureAwait(false);
                response = null;

                if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(currentUrl, location, out var next))
                {
                    _logger.LogError("Model download redirect for {Asset} had no usable Location header.", asset.RelativePath);
                    return null;
                }

                if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !IsAllowedDownloadHost(next.Host))
                {
                    _logger.LogError(
                        "Model download for {Asset} redirected to a non-allowlisted host '{Host}'.",
                        asset.RelativePath,
                        next.Host);
                    return null;
                }

                if (redirectCount >= MaxRedirects)
                {
                    _logger.LogError("Model download for {Asset} exceeded {Max} redirects.", asset.RelativePath, MaxRedirects);
                    return null;
                }

                redirectCount++;
                currentUrl = next;
            }

            if (response is null)
            {
                _logger.LogError("Model download for {Asset} produced no response.", asset.RelativePath);
                return null;
            }

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
            TryDelete(temp);
        }
    }

    private const int MaxRedirects = 5;

    private static bool IsAllowedDownloadHost(string host)
    {
        foreach (var allowed in PinnedModels.DownloadHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase) ||
                (host.Length > allowed.Length &&
                 host.EndsWith(allowed, StringComparison.OrdinalIgnoreCase) &&
                 host[host.Length - allowed.Length - 1] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PrereqInstallManifest))]
public sealed partial class PrereqJsonContext : JsonSerializerContext;
