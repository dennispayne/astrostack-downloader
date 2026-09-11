using System.Text.Json;
using System.Text.Json.Serialization;
using WgFetch.Core.Logging;

namespace WgFetch.Core.Output;

/// <summary>One candidate URL's verification outcome, redacted, for the provenance record.</summary>
public sealed record ProvenanceCandidate
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("verificationStatus")]
    public required string VerificationStatus { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }
}

/// <summary>
/// Per-package-version provenance: every candidate the discovery pipeline considered, what was
/// accepted, and how it was verified (docs/REQUIREMENTS.md, "Output layout" and "Discovery pipeline").
/// Every string field is redacted through <see cref="SecretRedactor"/> before it reaches this record —
/// a token or key must never reach <c>provenance.json</c>.
/// </summary>
public sealed record ProvenanceRecord
{
    [JsonPropertyName("packageIdentifier")]
    public required string PackageIdentifier { get; init; }

    [JsonPropertyName("componentId")]
    public string? ComponentId { get; init; }

    [JsonPropertyName("friendlyQuery")]
    public required string FriendlyQuery { get; init; }

    [JsonPropertyName("discoveryStage")]
    public required string DiscoveryStage { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<ProvenanceCandidate> Candidates { get; init; } = [];

    [JsonPropertyName("acceptedUrl")]
    public string? AcceptedUrl { get; init; }

    [JsonPropertyName("resolvedVersion")]
    public required string ResolvedVersion { get; init; }

    [JsonPropertyName("sourceVersions")]
    public IReadOnlyDictionary<string, string> SourceVersions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("upstreamSha256")]
    public string? UpstreamSha256 { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("resolutionTier")]
    public required string ResolutionTier { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    [JsonPropertyName("aiMode")]
    public string? AiMode { get; init; }

    [JsonPropertyName("recipeUsed")]
    public bool RecipeUsed { get; init; }

    [JsonPropertyName("recipeOrigin")]
    public string? RecipeOrigin { get; init; }

    /// <summary>Redacts every free-text field. Called by <see cref="ProvenanceWriter"/> before persisting.</summary>
    public ProvenanceRecord Redacted()
    {
        return this with
        {
            FriendlyQuery = SecretRedactor.Redact(FriendlyQuery),
            ComponentId = ComponentId is null ? null : SecretRedactor.Redact(ComponentId),
            AcceptedUrl = AcceptedUrl is null ? null : SecretRedactor.RedactUrl(AcceptedUrl),
            AiMode = AiMode is null ? null : SecretRedactor.Redact(AiMode),
            RecipeOrigin = RecipeOrigin is null ? null : SecretRedactor.Redact(RecipeOrigin),
            SourceVersions = SourceVersions.ToDictionary(
                kv => SecretRedactor.Redact(kv.Key),
                kv => SecretRedactor.Redact(kv.Value),
                StringComparer.Ordinal),
            Candidates = Candidates
                .Select(c => c with
                {
                    Url = SecretRedactor.RedactUrl(c.Url),
                    Rationale = c.Rationale is null ? null : SecretRedactor.Redact(c.Rationale),
                    Reason = SecretRedactor.Redact(c.Reason),
                })
                .ToList(),
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ProvenanceRecord>))]
internal sealed partial class ProvenanceJsonContext : JsonSerializerContext;

/// <summary>
/// Single writer for <c>provenance.json</c> at the output root. Re-running a fetch for the same
/// package+version replaces that record in place — merging is idempotent, never append-only
/// (docs/REQUIREMENTS.md, "Parallelism": "index.db, provenance.json, targets.yaml ... need a single
/// writer or a transaction per artifact").
/// </summary>
public sealed class ProvenanceWriter
{
    private readonly TimeProvider _timeProvider;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public ProvenanceWriter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Merges <paramref name="record"/> into <c>provenance.json</c>, replacing any existing record for
    /// the same <see cref="ProvenanceRecord.PackageIdentifier"/>+<see cref="ProvenanceRecord.ResolvedVersion"/>.
    /// The record is redacted before being written, regardless of whether the caller already redacted it.
    /// </summary>
    public async Task WriteAsync(string outputRoot, ProvenanceRecord record, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(record);

        var redacted = record.Redacted();
        var path = SourceLayout.ProvenancePath(outputRoot);

        await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            var replaced = existing
                .Where(r => !Matches(r, redacted))
                .Append(redacted)
                .OrderBy(r => r.PackageIdentifier, StringComparer.Ordinal)
                .ThenBy(r => r.ResolvedVersion, StringComparer.Ordinal)
                .ToList();

            // Serializing through the source-generated JsonTypeInfo keeps this reflection-free and
            // AOT-safe (the library is IsAotCompatible).
            var json = JsonSerializer.Serialize(replaced, ProvenanceJsonContext.Default.ListProvenanceRecord);
            await File.WriteAllTextAsync(path, json + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>Reads every record currently in <c>provenance.json</c>, or an empty list when absent.</summary>
    public async Task<IReadOnlyList<ProvenanceRecord>> ReadAllAsync(string outputRoot, CancellationToken cancellationToken)
    {
        var path = SourceLayout.ProvenancePath(outputRoot);
        return await LoadAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The current UTC instant, from the injected clock, for callers stamping a new record.</summary>
    public DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private static bool Matches(ProvenanceRecord a, ProvenanceRecord b) =>
        string.Equals(a.PackageIdentifier, b.PackageIdentifier, StringComparison.Ordinal) &&
        string.Equals(a.ResolvedVersion, b.ResolvedVersion, StringComparison.Ordinal);

    private static async Task<List<ProvenanceRecord>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(text, ProvenanceJsonContext.Default.ListProvenanceRecord) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
