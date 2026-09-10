namespace WgFetch.Core.Inference;

/// <summary>
/// Short structured text completion, for the pinned Phi-3.5-mini-instruct model (local) or a
/// configured OpenAI-compatible endpoint (remote) (docs/REQUIREMENTS.md, "AI execution modes").
/// Implementations must stream visible progress rather than sitting silent
/// (docs/REQUIREMENTS.md, "Responsiveness": "Phi inference must stream visible progress").
/// </summary>
public interface ITextGenerator
{
    /// <summary>
    /// Generates a short structured completion for <paramref name="prompt"/>. <paramref name="onProgress"/>,
    /// if supplied, is invoked with incremental output chunks as they become available.
    /// </summary>
    Task<string> GenerateAsync(string prompt, Action<string>? onProgress, CancellationToken cancellationToken);
}
