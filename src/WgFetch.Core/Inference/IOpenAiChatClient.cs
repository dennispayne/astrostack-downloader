namespace WgFetch.Core.Inference;

/// <summary>
/// A minimal POST-capable seam for the generic OpenAI-compatible chat/completions endpoint
/// (docs/REQUIREMENTS.md, "AI execution modes": "Optional remote inference via a generic
/// OpenAI-compatible endpoint"). <see cref="Abstractions.IHttpGateway"/> intentionally exposes only
/// <c>HEAD</c>/<c>GET</c> — it is the seam for download and verification traffic, which never needs a
/// request body. Chat completions require a JSON POST body, so this small additive interface lives
/// here rather than widening the shared gateway used by the verification gate and downloader.
/// </summary>
public interface IOpenAiChatClient
{
    /// <summary>
    /// Posts a chat/completions request. <paramref name="apiKeyHeaderValue"/>, if supplied, is sent
    /// as a bearer <c>Authorization</c> header; callers must never log its value.
    /// </summary>
    public Task<string> PostChatCompletionAsync(
        Uri endpoint,
        string jsonBody,
        string? apiKeyHeaderValue,
        CancellationToken cancellationToken);
}
