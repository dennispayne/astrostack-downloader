namespace WgFetch.Core.Targets;

/// <summary>
/// Per-entry acquisition state for a <c>targets.yaml</c> row (docs/REQUIREMENTS.md,
/// "Target list — repo-driven acquisition"). The vocabulary is "acquire/acquired" throughout;
/// "hydrate" is intentionally not used anywhere in this codebase.
/// </summary>
public enum TargetState
{
    /// <summary>Name only; nothing resolved yet.</summary>
    Listed,

    /// <summary>Package ID and a verified URL are known, but no bytes have been fetched.</summary>
    Resolved,

    /// <summary>Installer present, hashed, and a manifest emitted.</summary>
    Acquired,

    /// <summary>A newer upstream version exists than what was acquired.</summary>
    Stale,

    /// <summary>Recognised but auth-walled (P1, unsupported in P0).</summary>
    Blocked,
}

/// <summary>Converts <see cref="TargetState"/> to and from the lowercase spelling used in <c>targets.yaml</c>.</summary>
public static class TargetStateExtensions
{
    /// <summary>Renders the state using the lowercase spelling documented in <c>targets.yaml</c>.</summary>
    public static string ToYamlString(this TargetState state) => state switch
    {
        TargetState.Listed => "listed",
        TargetState.Resolved => "resolved",
        TargetState.Acquired => "acquired",
        TargetState.Stale => "stale",
        TargetState.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Parses the lowercase spelling used in <c>targets.yaml</c>, case-insensitively.</summary>
    public static TargetState ParseYamlState(string text) => text.Trim().ToLowerInvariant() switch
    {
        "listed" => TargetState.Listed,
        "resolved" => TargetState.Resolved,
        "acquired" => TargetState.Acquired,
        "stale" => TargetState.Stale,
        "blocked" => TargetState.Blocked,
        _ => throw new FormatException($"Unknown target state '{text}'."),
    };
}
