using WgFetch.Core.Versioning;

namespace WgFetch.Core.Output;

/// <summary>The on-disk artifacts for one acquired package version, as known to the pruner.</summary>
public sealed record AcquiredVersionArtifacts
{
    public required string Version { get; init; }

    /// <summary>Full path to the manifest directory for this version (deleted as a whole unit, or not at all).</summary>
    public required string ManifestDirectory { get; init; }

    /// <summary>Full paths to every installer file for this version.</summary>
    public IReadOnlyList<string> InstallerPaths { get; init; } = [];
}

/// <summary>Outcome of a prune pass for one package.</summary>
public sealed record RetentionPruneResult
{
    public required IReadOnlyList<string> KeptVersions { get; init; }

    public required IReadOnlyList<string> PrunedVersions { get; init; }

    public required IReadOnlyList<string> DeletedPaths { get; init; }
}

/// <summary>
/// Keeps the N most recent versions of an acquired package (default 2), pruning both installers and
/// manifests together so neither is ever orphaned, and never pruning a version referenced by a pinned
/// entry (docs/REQUIREMENTS.md, "Configuration and inputs": "Retention: keep N most recent versions,
/// default 2 ... never pruning a version referenced by a pinned entry").
///
/// <para>
/// Pruning is a serialized operation — running it concurrently with a fetch of the same package is a
/// race (docs/REQUIREMENTS.md, "Parallelism"), so all prune passes across this process share one lock.
/// </para>
/// </summary>
public sealed class RetentionPruner
{
    public const int DefaultKeepVersions = 2;

    private static readonly SemaphoreSlim PruneLock = new(1, 1);

    private readonly VersionComparator _comparator;

    public RetentionPruner(VersionComparator? comparator = null)
    {
        _comparator = comparator ?? VersionComparator.Instance;
    }

    /// <summary>
    /// Determines which versions to keep/prune and deletes the pruned ones' installers and manifest
    /// directories. Never deletes <paramref name="pinnedVersion"/>, and never deletes a manifest
    /// directory without also deleting its installers (or vice versa) — a version is removed wholly
    /// or not at all.
    /// </summary>
    public async Task<RetentionPruneResult> PruneAsync(
        IReadOnlyList<AcquiredVersionArtifacts> artifacts,
        string? pinnedVersion,
        int keepVersions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (keepVersions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keepVersions), keepVersions, "must keep at least one version");
        }

        await PruneLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ordered = artifacts
                .OrderByDescending(a => a.Version, _comparator)
                .ToList();

            var kept = new List<AcquiredVersionArtifacts>();
            var pruned = new List<AcquiredVersionArtifacts>();

            foreach (var artifact in ordered)
            {
                var isPinned = pinnedVersion is not null &&
                    string.Equals(artifact.Version, pinnedVersion, StringComparison.Ordinal);

                if (isPinned || kept.Count < keepVersions)
                {
                    kept.Add(artifact);
                }
                else
                {
                    pruned.Add(artifact);
                }
            }

            // Re-check: if the pinned version fell outside the natural window it was already added above
            // by the isPinned branch, so `kept` may exceed keepVersions by at most one — that is correct,
            // never pruning a pinned version even when it is stale.
            var deleted = new List<string>();
            foreach (var artifact in pruned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteVersion(artifact, deleted);
            }

            return new RetentionPruneResult
            {
                KeptVersions = kept.Select(a => a.Version).ToList(),
                PrunedVersions = pruned.Select(a => a.Version).ToList(),
                DeletedPaths = deleted,
            };
        }
        finally
        {
            PruneLock.Release();
        }
    }

    public Task<RetentionPruneResult> PruneAsync(
        IReadOnlyList<AcquiredVersionArtifacts> artifacts,
        string? pinnedVersion,
        CancellationToken cancellationToken) =>
        PruneAsync(artifacts, pinnedVersion, DefaultKeepVersions, cancellationToken);

    private static void DeleteVersion(AcquiredVersionArtifacts artifact, List<string> deleted)
    {
        foreach (var installer in artifact.InstallerPaths)
        {
            if (File.Exists(installer))
            {
                File.Delete(installer);
                deleted.Add(installer);
            }
        }

        if (Directory.Exists(artifact.ManifestDirectory))
        {
            Directory.Delete(artifact.ManifestDirectory, recursive: true);
            deleted.Add(artifact.ManifestDirectory);
        }
    }
}
