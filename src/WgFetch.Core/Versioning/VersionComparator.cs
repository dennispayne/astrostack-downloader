namespace WgFetch.Core.Versioning;

/// <summary>Outcome of a single version comparison, including the branch that decided it.</summary>
/// <param name="Order">Negative if left precedes right, zero if equal, positive if left follows right.</param>
/// <param name="Decidable">
/// False when the comparator cannot meaningfully order the two versions (e.g. a date-stamped version
/// against a dotted-numeric one). The order is still deterministic and total so sorting stays stable,
/// but callers may escalate to the LLM tier and must record that they did.
/// </param>
/// <param name="Reason">Human-readable description of the branch that decided the comparison.</param>
public readonly record struct VersionComparison(int Order, bool Decidable, string Reason);

/// <summary>
/// Deterministic, total, antisymmetric and transitive ordering over vendor version strings
/// (docs/REQUIREMENTS.md, "Version handling"). The LLM is only consulted when
/// <see cref="VersionComparison.Decidable"/> is false.
/// </summary>
public sealed class VersionComparator : IComparer<VersionValue>, IComparer<string>
{
    public static VersionComparator Instance { get; } = new();

    public int Compare(VersionValue? x, VersionValue? y) => CompareDetailed(x, y).Order;

    public int Compare(string? x, string? y) =>
        CompareDetailed(VersionValue.Parse(x), VersionValue.Parse(y)).Order;

    public VersionComparison CompareDetailed(string? left, string? right) =>
        CompareDetailed(VersionValue.Parse(left), VersionValue.Parse(right));

    public VersionComparison CompareDetailed(VersionValue? left, VersionValue? right)
    {
        if (left is null && right is null)
        {
            return new VersionComparison(0, true, "both versions absent");
        }

        if (left is null)
        {
            return new VersionComparison(-1, true, "left version absent");
        }

        if (right is null)
        {
            return new VersionComparison(1, true, "right version absent");
        }

        if (string.Equals(left.Raw, right.Raw, StringComparison.Ordinal))
        {
            return new VersionComparison(0, true, "identical version strings");
        }

        var decidable = IsComparableKind(left.Kind, right.Kind);
        var reasonPrefix = decidable
            ? string.Empty
            : $"incomparable version shapes ({left.Kind} vs {right.Kind}); ";

        var coreOrder = CompareTokens(left.Core, right.Core, out var coreReason, out var coreAmbiguous);
        if (coreOrder != 0)
        {
            return new VersionComparison(
                Sign(coreOrder),
                decidable && !coreAmbiguous,
                (coreAmbiguous ? "ambiguous suffix; " : string.Empty) + reasonPrefix + coreReason);
        }

        if (left.HasPreRelease != right.HasPreRelease)
        {
            // A release always outranks a pre-release of the same core.
            var order = left.HasPreRelease ? -1 : 1;
            return new VersionComparison(order, decidable, reasonPrefix + "pre-release ranks below release");
        }

        if (left.HasPreRelease)
        {
            var preOrder = CompareTokens(left.PreRelease, right.PreRelease, out var preReason, out var preAmbiguous);
            if (preOrder != 0)
            {
                return new VersionComparison(
                    Sign(preOrder),
                    decidable && !preAmbiguous,
                    reasonPrefix + "pre-release: " + preReason);
            }
        }

        // Every component matched: the two spellings denote the same version ("1.2" == "1.2.0",
        // "v3.2.0" == "3.2.0", and semver build metadata never participates in precedence).
        return new VersionComparison(0, decidable, reasonPrefix + "equal after normalization");
    }

    /// <summary>Returns the newest of a set of versions, or null when the set is empty.</summary>
    public VersionValue? Newest(IEnumerable<VersionValue> versions)
    {
        VersionValue? best = null;
        foreach (var candidate in versions)
        {
            if (best is null || CompareDetailed(candidate, best).Order > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool IsComparableKind(VersionKind left, VersionKind right)
    {
        if (left == VersionKind.Opaque || right == VersionKind.Opaque)
        {
            return false;
        }

        // Date-stamped versions cannot be meaningfully ordered against non-date versions.
        return (left == VersionKind.Date) == (right == VersionKind.Date);
    }

    /// <param name="ambiguous">
    /// Set when the ordering was decided by a trailing alphabetic suffix against nothing at all
    /// (<c>1.2 HF3</c> vs <c>1.2</c>, <c>4.1b7</c> vs <c>4.1</c>). A hotfix suffix ranks above its base
    /// version while a beta suffix ranks below it, and the difference is not mechanically knowable, so
    /// the order stays deterministic but the caller is told it may escalate to the LLM tier.
    /// </param>
    private static int CompareTokens(
        IReadOnlyList<VersionToken> left,
        IReadOnlyList<VersionToken> right,
        out string reason,
        out bool ambiguous)
    {
        ambiguous = false;
        var max = Math.Max(left.Count, right.Count);
        for (var i = 0; i < max; i++)
        {
            var hasLeft = i < left.Count;
            var hasRight = i < right.Count;

            if (!hasLeft)
            {
                // Absent component is treated as zero: 1.2 == 1.2.0 but 1.2 < 1.2.1.
                var r = right[i];
                if (r.IsNumeric && r.Number == 0)
                {
                    continue;
                }

                reason = $"component {i + 1} absent on the left, '{r.Text}' on the right";
                ambiguous = !r.IsNumeric;
                return r.IsNumeric ? -1 : 1; // trailing alpha (e.g. "1.2b") ranks below "1.2"
            }

            if (!hasRight)
            {
                var l = left[i];
                if (l.IsNumeric && l.Number == 0)
                {
                    continue;
                }

                reason = $"component {i + 1} '{l.Text}' on the left, absent on the right";
                ambiguous = !l.IsNumeric;
                return l.IsNumeric ? 1 : -1;
            }

            var a = left[i];
            var b = right[i];

            if (a.IsNumeric && b.IsNumeric)
            {
                var cmp = a.Number.CompareTo(b.Number);
                if (cmp != 0)
                {
                    reason = $"component {i + 1}: {a.Number} vs {b.Number}";
                    return cmp;
                }

                continue;
            }

            if (!a.IsNumeric && !b.IsNumeric)
            {
                var cmp = string.CompareOrdinal(a.Text, b.Text);
                if (cmp != 0)
                {
                    reason = $"component {i + 1}: '{a.Text}' vs '{b.Text}'";
                    return cmp;
                }

                continue;
            }

            // Numeric outranks alphabetic at the same position (1.2.1 > 1.2.rc).
            reason = a.IsNumeric
                ? $"component {i + 1}: numeric {a.Number} outranks '{b.Text}'"
                : $"component {i + 1}: numeric {b.Number} outranks '{a.Text}'";
            return a.IsNumeric ? 1 : -1;
        }

        reason = "all components equal";
        return 0;
    }

    private static int Sign(int value) => value < 0 ? -1 : value > 0 ? 1 : 0;
}
