namespace WgFetch.Core.Inference;

/// <summary>
/// A deterministic, hash-based fake embedding model so resolution tests never load real ONNX weights
/// (docs/REQUIREMENTS.md, "Testing": "deterministic model-inference fakes so resolution tests never
/// load real ONNX weights"). Produces stable, L2-normalized vectors derived from simple token overlap
/// so semantically similar strings (e.g. sharing words) score higher cosine similarity, without
/// depending on any real embedding semantics.
/// </summary>
public sealed class FakeEmbeddingModel : IEmbeddingModel
{
    private const int Dimensions = 64;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            vectors[i] = Embed(texts[i]);
        }

        return Task.FromResult(vectors);
    }

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        var normalized = StripPrefix(text).ToLowerInvariant();
        var tokens = normalized.Split(
            [' ', '\t', '\n', '\r', '.', '-', '_', '/', '(', ')', '\''],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            var bucket = Math.Abs(StableHash(token)) % Dimensions;
            vector[bucket] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    private static string StripPrefix(string text) =>
        text.StartsWith("passage: ", StringComparison.Ordinal) ? text["passage: ".Length..] :
        text.StartsWith("query: ", StringComparison.Ordinal) ? text["query: ".Length..] :
        text;

    /// <summary>FNV-1a — stable across runs and platforms, unlike <see cref="string.GetHashCode()"/>.</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return hash;
        }
    }
}

/// <summary>
/// A scripted <see cref="ITextGenerator"/> for tests: returns queued canned responses in order,
/// optionally throwing or delaying to exercise timeout/escalation and malformed-output handling.
/// </summary>
public sealed class ScriptedTextGenerator : ITextGenerator
{
    private readonly Queue<Func<CancellationToken, Task<string>>> _script = new();

    public ScriptedTextGenerator Enqueue(string response)
    {
        _script.Enqueue(_ => Task.FromResult(response));
        return this;
    }

    public ScriptedTextGenerator EnqueueDelay(TimeSpan delay, string response)
    {
        _script.Enqueue(async ct =>
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return response;
        });
        return this;
    }

    public ScriptedTextGenerator EnqueueThrow(Exception exception)
    {
        _script.Enqueue(_ => throw exception);
        return this;
    }

    public int CallCount { get; private set; }

    public async Task<string> GenerateAsync(string prompt, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        CallCount++;
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("ScriptedTextGenerator has no more queued responses.");
        }

        var step = _script.Dequeue();
        var result = await step(cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke(result);
        return result;
    }
}
