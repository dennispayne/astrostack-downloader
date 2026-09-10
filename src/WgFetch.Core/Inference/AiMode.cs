namespace WgFetch.Core.Inference;

/// <summary>
/// Selects where generative inference runs (docs/REQUIREMENTS.md, "AI execution modes").
/// <c>auto</c> prefers local and escalates to remote only if remote is configured and the local tier
/// fails or exceeds a timeout — never a silent fallback in <see cref="Local"/> or <see cref="Remote"/> mode.
/// </summary>
public enum AiMode
{
    /// <summary>Offline-local is the default: all inference runs on-box, no network AI calls.</summary>
    Local,

    /// <summary>Opt-in only: every generation call goes to the configured remote endpoint.</summary>
    Remote,

    /// <summary>Prefers local; escalates to remote only if configured and local fails or times out.</summary>
    Auto,
}
