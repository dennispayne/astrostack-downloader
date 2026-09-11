using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Abstractions;
using WgFetch.Core.Model;
using WgFetch.Core.Versioning;
using YamlDotNet.RepresentationModel;

namespace WgFetch.Core.Discovery;

/// <summary>
/// Structured-upstream discovery via <c>microsoft/winget-pkgs</c> manifests, fetched as raw content
/// over <see cref="IHttpGateway"/> rather than requiring an installed winget source
/// (docs/REQUIREMENTS.md, "Discovery pipeline", stage 3; "Prior art": borrow winget-pkgs conventions).
/// Most target astro apps are absent from winget-pkgs or stale, so this stage frequently contributes
/// nothing — that is expected, not an error.
/// </summary>
public sealed class WingetStage : IDiscoveryStage
{
    private const string Repository = "microsoft/winget-pkgs";

    private readonly IHttpGateway _http;
    private readonly ILogger _logger;

    public WingetStage(IHttpGateway http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger.Instance;
    }

    public DiscoveryStage Stage => DiscoveryStage.WingetSource;

    public async Task<StageOutcome> TryResolveAsync(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.KnownWingetPackageId))
        {
            return StageOutcome.Empty(Stage);
        }

        return await ResolveAsync(_http, request.KnownWingetPackageId, Stage, _logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Used by <see cref="RecipeBackedStage"/> when a recipe's SourceKind is WingetSource.</summary>
    internal static Task<StageOutcome> ResolveFromRecipeAsync(
        IHttpGateway http,
        Recipe recipe,
        DiscoveryStage taggedStage,
        ILogger? logger,
        CancellationToken cancellationToken) =>
        ResolveAsync(http, recipe.PackageId, taggedStage, logger, cancellationToken, recipe.Allowlist);

    private static async Task<StageOutcome> ResolveAsync(
        IHttpGateway http,
        string packageId,
        DiscoveryStage taggedStage,
        ILogger? logger,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? allowlistOverride = null)
    {
        logger ??= NullLogger.Instance;
        var parts = packageId.Split('.', 2);
        if (parts.Length < 2)
        {
            return StageOutcome.Empty(taggedStage, $"'{packageId}' is not a winget-shaped PackageIdentifier ('Publisher.Package').");
        }

        var publisher = parts[0];
        var package = parts[1];
        var basePath = $"manifests/{char.ToLowerInvariant(package[0])}/{publisher}/{package}";

        var versionDirs = await ListDirectoryNamesAsync(http, basePath, cancellationToken).ConfigureAwait(false);
        if (versionDirs is null)
        {
            return StageOutcome.Empty(taggedStage, $"no winget-pkgs manifest found at '{basePath}'.");
        }

        var versions = versionDirs
            .Select(VersionValue.Parse)
            .ToList();

        var newest = VersionComparator.Instance.Newest(versions);
        if (newest is null)
        {
            return StageOutcome.Empty(taggedStage, $"winget-pkgs manifest directory for '{packageId}' has no version subdirectories.");
        }

        var versionPath = $"{basePath}/{newest.Raw}";
        var files = await ListDirectoryEntriesAsync(http, versionPath, cancellationToken).ConfigureAwait(false);
        var installerFile = files?.FirstOrDefault(f => f.Name.EndsWith(".installer.yaml", StringComparison.OrdinalIgnoreCase));
        if (installerFile is null)
        {
            return StageOutcome.Empty(taggedStage, $"no installer manifest found under '{versionPath}'.");
        }

        var yaml = await FetchRawAsync(http, installerFile.DownloadUrl, cancellationToken).ConfigureAwait(false);
        if (yaml is null)
        {
            return StageOutcome.Empty(taggedStage, $"could not fetch installer manifest '{installerFile.DownloadUrl}'.");
        }

        var candidates = ParseInstallerManifest(yaml, taggedStage, newest.Raw, logger);
        if (candidates.Count == 0)
        {
            return StageOutcome.Empty(taggedStage, $"installer manifest '{installerFile.DownloadUrl}' had no usable InstallerUrl entries.");
        }

        var allowlist = allowlistOverride is { Count: > 0 }
            ? allowlistOverride
            : candidates.Select(c => c.Url.Host).Distinct().ToArray();

        return new StageOutcome
        {
            Stage = taggedStage,
            Allowlist = allowlist,
            Candidates = candidates,
            Version = newest.Raw,
        };
    }

    private static List<DiscoveryCandidate> ParseInstallerManifest(string yaml, DiscoveryStage stage, string version, ILogger logger)
    {
        var candidates = new List<DiscoveryCandidate>();
        try
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(yaml))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode manifest ||
                !manifest.Children.TryGetValue(new YamlScalarNode("Installers"), out var installersNode) ||
                installersNode is not YamlSequenceNode installers)
            {
                return candidates;
            }

            foreach (var entry in installers)
            {
                if (entry is not YamlMappingNode installer)
                {
                    continue;
                }

                var urlText = Scalar(installer, "InstallerUrl");
                if (urlText is null || !Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                var arch = Scalar(installer, "Architecture");
                var sha = Scalar(installer, "InstallerSha256");

                candidates.Add(new DiscoveryCandidate
                {
                    Url = uri,
                    Version = version,
                    Stage = stage,
                    Rationale = $"winget manifest installer ({arch ?? "unspecified arch"})",
                    UpstreamSha256 = sha,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to parse winget installer manifest: {Message}", ex.Message);
        }

        return candidates;
    }

    /// <summary>Reads a scalar child by key, or null when absent or not a scalar.</summary>
    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private sealed record ContentEntry(string Name, string DownloadUrl, bool IsDirectory);

    private static async Task<IReadOnlyList<string>?> ListDirectoryNamesAsync(IHttpGateway http, string path, CancellationToken cancellationToken)
    {
        var entries = await ListDirectoryEntriesAsync(http, path, cancellationToken, dirsOnly: true).ConfigureAwait(false);
        return entries?.Select(e => e.Name).ToArray();
    }

    private static async Task<IReadOnlyList<ContentEntry>?> ListDirectoryEntriesAsync(
        IHttpGateway http,
        string path,
        CancellationToken cancellationToken,
        bool dirsOnly = false)
    {
        var url = new Uri($"https://api.github.com/repos/{Repository}/contents/{path}");
        HttpResponseSpec response;
        try
        {
            response = await http.SendAsync(
                new HttpRequestSpec
                {
                    Url = url,
                    Verb = HttpVerb.Get,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Accept"] = "application/vnd.github+json",
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        await using (response.ConfigureAwait(false))
        {
            if (!response.IsSuccess)
            {
                return null;
            }

            using var reader = new StreamReader(response.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var results = new List<ContentEntry>();
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var downloadUrl = item.TryGetProperty("download_url", out var d) ? d.GetString() : null;

                    if (name is null)
                    {
                        continue;
                    }

                    var isDir = string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase);
                    if (dirsOnly && !isDir)
                    {
                        continue;
                    }

                    results.Add(new ContentEntry(name, downloadUrl ?? string.Empty, isDir));
                }

                return results;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static async Task<string?> FetchRawAsync(IHttpGateway http, string downloadUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(downloadUrl) || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            var response = await http.SendAsync(new HttpRequestSpec { Url = uri, Verb = HttpVerb.Get }, cancellationToken).ConfigureAwait(false);
            await using (response.ConfigureAwait(false))
            {
                if (!response.IsSuccess)
                {
                    return null;
                }

                using var reader = new StreamReader(response.Body);
                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
