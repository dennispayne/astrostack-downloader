using YamlDotNet.RepresentationModel;

namespace WgFetch.Core.Targets;

/// <summary>
/// The parsed form of a repo's root-level <c>targets.yaml</c> (docs/REQUIREMENTS.md,
/// "Target list — repo-driven acquisition"). A populated-but-unacquired document is valid and
/// expected — nothing in this type treats unacquired entries as an error condition.
/// </summary>
public sealed class TargetsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int Version { get; set; } = CurrentSchemaVersion;

    public List<TargetEntry> Targets { get; init; } = new();

    /// <summary>
    /// Top-level keys other than <c>version</c>/<c>targets</c>, preserved verbatim for round-trip
    /// (docs/REQUIREMENTS.md: "Unknown keys must round-trip untouched").
    /// </summary>
    public IReadOnlyDictionary<string, YamlNode> ExtraFields { get; set; } =
        new Dictionary<string, YamlNode>(StringComparer.Ordinal);

    /// <summary>Finds an entry by name, case-insensitively.</summary>
    public TargetEntry? Find(string name) =>
        Targets.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Appends a new <see cref="TargetState.Listed"/> entry for <paramref name="name"/>, unless one
    /// already exists (case-insensitively), in which case the existing entry is returned unchanged.
    /// This is what <c>wgfetch add &lt;name&gt;...</c> calls per name (docs/REQUIREMENTS.md).
    /// </summary>
    public TargetEntry Add(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        TargetEntry? existing = Find(name);
        if (existing is not null)
        {
            return existing;
        }

        var entry = new TargetEntry { Name = name, State = TargetState.Listed };
        Targets.Add(entry);
        return entry;
    }

    /// <summary>Removes the entry named <paramref name="name"/> (case-insensitively), if present.</summary>
    public bool Remove(string name)
    {
        TargetEntry? existing = Find(name);
        return existing is not null && Targets.Remove(existing);
    }

    /// <summary>
    /// Entries that <c>wgfetch fetch --winget-repo &lt;path&gt;</c> should act on (docs/REQUIREMENTS.md,
    /// "Target list — repo-driven acquisition"): by default every <see cref="TargetState.Listed"/>,
    /// <see cref="TargetState.Resolved"/> or <see cref="TargetState.Stale"/> entry.
    /// <list type="bullet">
    /// <item><paramref name="onlyMissing"/> ("<c>--only-missing</c>") skips stale refresh, leaving only
    /// entries that have never been acquired at all (<see cref="TargetState.Listed"/> /
    /// <see cref="TargetState.Resolved"/>).</item>
    /// <item><paramref name="refreshStale"/> ("<c>--refresh-stale</c>") narrows the run to entries that
    /// are already acquired or already known stale (<see cref="TargetState.Acquired"/> /
    /// <see cref="TargetState.Stale"/>), i.e. a refresh pass rather than a first acquisition.</item>
    /// </list>
    /// The two flags are mutually exclusive at the CLI layer; if both are supplied here the result is
    /// their intersection (only <see cref="TargetState.Stale"/> entries).
    /// </summary>
    public IReadOnlyList<TargetEntry> EntriesNeedingAcquisition(bool onlyMissing = false, bool refreshStale = false)
    {
        IEnumerable<TargetEntry> query = Targets;

        if (refreshStale)
        {
            query = query.Where(t => t.State is TargetState.Acquired or TargetState.Stale);
        }
        else
        {
            query = query.Where(t => t.State is TargetState.Listed or TargetState.Resolved or TargetState.Stale);
        }

        if (onlyMissing)
        {
            query = query.Where(t => t.State != TargetState.Stale);
        }

        return query.ToList();
    }
}
