using YamlDotNet.RepresentationModel;

namespace WgFetch.Core.Targets;

/// <summary>
/// One row of <c>targets.yaml</c> (docs/REQUIREMENTS.md, "Target list — repo-driven acquisition").
/// <c>targets.yaml</c> is human-editable and round-trippable, so this type is a mutable bag of
/// fields rather than an immutable record: hand edits and successive resolution/acquisition steps
/// both mutate it in place.
/// </summary>
public sealed class TargetEntry
{
    /// <summary>The only mandatory field; matched case-insensitively by <see cref="TargetsDocument"/>.</summary>
    public required string Name { get; set; }

    /// <summary>winget <c>PackageIdentifier</c>, e.g. <c>AstroStack.NINA</c>. Filled on first successful resolution.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// Lowercase astrostack-dsc component slug, e.g. <c>nina</c>. Recorded alongside <see cref="Id"/>
    /// so the join key between winget's <c>PackageIdentifier</c> and astrostack-dsc's component id is
    /// never ambiguous (docs/REQUIREMENTS.md, "Consumer alignment — astrostack-dsc").
    /// </summary>
    public string? ComponentId { get; set; }

    public TargetState State { get; set; } = TargetState.Listed;

    public string? AcquiredVersion { get; set; }

    public string? AvailableVersion { get; set; }

    /// <summary>Optional architecture override; defaults to the host architecture when absent.</summary>
    public string? Arch { get; set; }

    public string? Scope { get; set; }

    /// <summary>Optional exact version pin.</summary>
    public string? Pin { get; set; }

    /// <summary>Vendor domains this package's installer may come from.</summary>
    public List<string> Allowlist { get; set; } = new();

    /// <summary>Optional inline recipe expression or a path to a recipe file.</summary>
    public string? Recipe { get; set; }

    public DateTimeOffset? LastAttempt { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Keys present in the on-disk YAML mapping for this entry that are not modelled above. Preserved
    /// verbatim (as the original <see cref="YamlNode"/>) and re-emitted on save so a future schema
    /// version, or a human hand-editing the file, never loses data it doesn't understand
    /// (docs/REQUIREMENTS.md: "Unknown keys must round-trip untouched").
    /// </summary>
    public IReadOnlyDictionary<string, YamlNode> ExtraFields { get; set; } =
        new Dictionary<string, YamlNode>(StringComparer.Ordinal);
}
