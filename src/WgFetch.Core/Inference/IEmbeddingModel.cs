namespace WgFetch.Core.Inference;

/// <summary>
/// Batch text embedding, for the pinned E5-small-v2 model (docs/REQUIREMENTS.md, "Name resolution").
/// <para>
/// <b>E5 prefix contract:</b> callers MUST prepend <c>"passage: "</c> to catalog entries being indexed
/// and <c>"query: "</c> to user input being resolved — omitting these measurably degrades accuracy.
/// Implementations must mean-pool the token embeddings and then L2-normalize the resulting vector so
/// callers can compare with a plain dot product as cosine similarity.
/// </para>
/// </summary>
public interface IEmbeddingModel
{
    /// <summary>
    /// Embeds a batch of already-prefixed strings. Returns one mean-pooled, L2-normalized vector per
    /// input, in input order.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
