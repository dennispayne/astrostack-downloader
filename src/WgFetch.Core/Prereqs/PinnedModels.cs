namespace WgFetch.Core.Prereqs;

/// <summary>One file belonging to a pinned model.</summary>
/// <param name="RelativePath">Path under the model directory.</param>
/// <param name="Url">Hugging Face download URL pinned to an immutable revision.</param>
/// <param name="SizeBytes">Expected size, when known.</param>
/// <param name="Sha256">
/// Digest compiled into the binary. When null the digest has not been pinned yet and the installer
/// **refuses to install the model** — it fails closed rather than accepting unverified weights.
/// See "Known conflicts" in docs/REQUIREMENTS.md.
/// </param>
public sealed record ModelAsset(string RelativePath, string Url, long? SizeBytes, string? Sha256)
{
    public bool IsPinned => !string.IsNullOrWhiteSpace(Sha256);
}

/// <summary>A pinned model: the exact repository, revision and files wgfetch will accept.</summary>
public sealed record PinnedModel(
    string Id,
    string DisplayName,
    string Repository,
    string Revision,
    bool IsLanguageModel,
    IReadOnlyList<ModelAsset> Assets)
{
    public bool FullyPinned => Assets.All(a => a.IsPinned);
}

/// <summary>
/// The models wgfetch will load, pinned by repository and revision
/// (docs/REQUIREMENTS.md, "Stack" and "Prerequisites — frictionless first run").
/// </summary>
public static class PinnedModels
{
    public const string EmbeddingModelId = "e5-small-v2";
    public const string LanguageModelId = "phi-3.5-mini-instruct-onnx";

    private const string HuggingFace = "https://huggingface.co";

    /// <summary>E5-small-v2, ONNX int8, used for tier-1 embedding name resolution.</summary>
    public static PinnedModel Embedding { get; } = new(
        EmbeddingModelId,
        "E5-small-v2 (ONNX int8)",
        "intfloat/e5-small-v2",
        "main",
        IsLanguageModel: false,
        [
            new ModelAsset("model.onnx", $"{HuggingFace}/intfloat/e5-small-v2/resolve/main/onnx/model_quantized.onnx", null, null),
            new ModelAsset("tokenizer.json", $"{HuggingFace}/intfloat/e5-small-v2/resolve/main/tokenizer.json", null, null),
            new ModelAsset("vocab.txt", $"{HuggingFace}/intfloat/e5-small-v2/resolve/main/vocab.txt", null, null),
        ]);

    /// <summary>Phi-3.5-mini-instruct, CPU int4 RTN block-32, used for the tier-2 fallback.</summary>
    public static PinnedModel LanguageModel { get; } = new(
        LanguageModelId,
        "Phi-3.5-mini-instruct-onnx (cpu-int4-rtn-block-32)",
        "microsoft/Phi-3.5-mini-instruct-onnx",
        "main",
        IsLanguageModel: true,
        [
            new ModelAsset(
                "model.onnx",
                $"{HuggingFace}/microsoft/Phi-3.5-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/phi-3.5-mini-instruct-cpu-int4-rtn-block-32-acc-level-4.onnx",
                null,
                null),
            new ModelAsset(
                "model.onnx.data",
                $"{HuggingFace}/microsoft/Phi-3.5-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/phi-3.5-mini-instruct-cpu-int4-rtn-block-32-acc-level-4.onnx.data",
                null,
                null),
            new ModelAsset(
                "genai_config.json",
                $"{HuggingFace}/microsoft/Phi-3.5-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/genai_config.json",
                null,
                null),
            new ModelAsset(
                "tokenizer.json",
                $"{HuggingFace}/microsoft/Phi-3.5-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/tokenizer.json",
                null,
                null),
        ]);

    public static IReadOnlyList<PinnedModel> All { get; } = [Embedding, LanguageModel];

    public static PinnedModel? Find(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hosts contacted by <c>wgfetch prereqs install</c>; documented in the README.</summary>
    public static IReadOnlyList<string> DownloadHosts { get; } = ["huggingface.co", "cdn-lfs.huggingface.co"];
}
