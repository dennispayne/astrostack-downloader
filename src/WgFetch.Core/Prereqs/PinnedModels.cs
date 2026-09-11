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

    /// <summary>
    /// Commit SHAs, not <c>main</c>: a digest pinned against a moving ref is not a pin at all, because
    /// the ref can advance to different bytes that then fail verification (or worse, silently change
    /// what "the pinned model" means).
    /// </summary>
    private const string EmbeddingRevision = "ffb93f3bd4047442299a41ebb6fa998a38507c52";

    private const string LanguageModelRevision = "7230dcd6c1dd28aab70f263ecc8734ec9d9bcb70";

    private const string LanguageModelBase =
        $"{HuggingFace}/microsoft/Phi-3.5-mini-instruct-onnx/resolve/{LanguageModelRevision}" +
        "/cpu_and_mobile/cpu-int4-awq-block-128-acc-level-4";

    /// <summary>
    /// E5-small-v2, ONNX graph-optimized (O4), used for tier-1 embedding name resolution. The plain
    /// <c>onnx/model_O4.onnx</c> variant is chosen over <c>onnx/model_qint8_avx512_vnni.onnx</c>
    /// deliberately: the int8 build needs AVX512-VNNI, which the N5105 reference box does not have.
    /// </summary>
    public static PinnedModel Embedding { get; } = new(
        EmbeddingModelId,
        "E5-small-v2 (ONNX O4)",
        "intfloat/e5-small-v2",
        EmbeddingRevision,
        IsLanguageModel: false,
        [
            new ModelAsset(
                "model.onnx",
                $"{HuggingFace}/intfloat/e5-small-v2/resolve/{EmbeddingRevision}/onnx/model_O4.onnx",
                66578744,
                "5a0ed8686280e292eec2321fbe06f21c3c53ed434ce5548712354211ef9e70bc"),
            new ModelAsset(
                "tokenizer.json",
                $"{HuggingFace}/intfloat/e5-small-v2/resolve/{EmbeddingRevision}/tokenizer.json",
                711396,
                "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66"),
            new ModelAsset(
                "vocab.txt",
                $"{HuggingFace}/intfloat/e5-small-v2/resolve/{EmbeddingRevision}/vocab.txt",
                231508,
                "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
        ]);

    /// <summary>
    /// Phi-3.5-mini-instruct, CPU int4 AWQ block-128 (accuracy level 4), used for the tier-2 fallback.
    /// The older <c>cpu-int4-rtn-block-32-acc-level-4</c> directory no longer exists upstream.
    /// </summary>
    public static PinnedModel LanguageModel { get; } = new(
        LanguageModelId,
        "Phi-3.5-mini-instruct-onnx (cpu-int4-awq-block-128)",
        "microsoft/Phi-3.5-mini-instruct-onnx",
        LanguageModelRevision,
        IsLanguageModel: true,
        [
            new ModelAsset(
                "model.onnx",
                $"{LanguageModelBase}/phi-3.5-mini-instruct-cpu-int4-awq-block-128-acc-level-4.onnx",
                52176615,
                "c4f05e6ef52f2588df181e566afbf5e8eeba097fece2fc8246770473a10225fd"),
            new ModelAsset(
                "model.onnx.data",
                $"{LanguageModelBase}/phi-3.5-mini-instruct-cpu-int4-awq-block-128-acc-level-4.onnx.data",
                2728144896,
                "3351fe9cc669eba43e07fb3cec436078629d5145531a28bc36fe6d5ad7683eb8"),
            new ModelAsset(
                "genai_config.json",
                $"{LanguageModelBase}/genai_config.json",
                1580,
                "d1036a44e904c816c864931b961483eccf18e985dbd6797eecb33e01b626f580"),
            new ModelAsset(
                "tokenizer.json",
                $"{LanguageModelBase}/tokenizer.json",
                1844436,
                "d0f067e1e15cd0a36ebef3668024882cb67a80b86fb4b7b4b128481f0d474db7"),
        ]);

    public static IReadOnlyList<PinnedModel> All { get; } = [Embedding, LanguageModel];

    public static PinnedModel? Find(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Hosts contacted by <c>wgfetch prereqs install</c>; documented in the README. Hugging Face serves
    /// the metadata request from <c>huggingface.co</c> and then redirects large (LFS/Xet-backed) blobs
    /// to a CDN host, so an allowlist that covers only the first host will fail to fetch any weights.
    /// </summary>
    public static IReadOnlyList<string> DownloadHosts { get; } =
    [
        "huggingface.co",
        "us.aws.cdn.hf.co",
        "cas-bridge.xethub.hf.co",
        "cdn-lfs.huggingface.co",
        "cdn-lfs-us-1.huggingface.co",
    ];
}
