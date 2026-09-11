using System.Text.Json;
using System.Text.Json.Serialization;

namespace WgFetch.Core.Logging;

/// <summary>
/// A machine-readable event emitted on stdout under <c>--json</c>. All human-readable logging goes to
/// stderr so piping stays clean, and event ordering is deterministic regardless of completion order
/// (docs/REQUIREMENTS.md, "Observability and diagnostics").
/// </summary>
public sealed record JsonEvent
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>Correlation ID so interleaved parallel work is reconstructible from a flat log.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("stage")]
    public string? Stage { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("elapsedMs")]
    public double? ElapsedMs { get; init; }
}

/// <summary>Writes <see cref="JsonEvent"/>s as newline-delimited JSON, redacting every field.</summary>
public sealed class JsonEventWriter
{
    private readonly TextWriter _writer;
    private readonly IReadOnlyList<string> _secrets;
    private readonly object _gate = new();
    private readonly List<JsonEvent> _buffer = [];
    private readonly bool _deterministicOrder;

    public JsonEventWriter(TextWriter? writer = null, IEnumerable<string>? secrets = null, bool deterministicOrder = false)
    {
        _writer = writer ?? Console.Out;
        _secrets = secrets?.ToArray() ?? Array.Empty<string>();
        _deterministicOrder = deterministicOrder;
    }

    /// <summary>Events written so far, in emission order.</summary>
    public IReadOnlyList<JsonEvent> Emitted
    {
        get
        {
            lock (_gate)
            {
                return _buffer.ToArray();
            }
        }
    }

    public void Write(JsonEvent @event)
    {
        var redacted = Redact(@event);
        lock (_gate)
        {
            _buffer.Add(redacted);
            if (!_deterministicOrder)
            {
                _writer.WriteLine(Serialize(redacted));
            }
        }
    }

    /// <summary>
    /// Flushes buffered events sorted by target then event name, so <c>--json</c> ordering does not
    /// depend on which parallel operation happened to finish first.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_deterministicOrder)
            {
                foreach (var @event in _buffer
                             .OrderBy(e => e.Target ?? string.Empty, StringComparer.Ordinal)
                             .ThenBy(e => e.Event, StringComparer.Ordinal))
                {
                    _writer.WriteLine(Serialize(@event));
                }

                _buffer.Clear();
            }

            _writer.Flush();
        }
    }

    private string Serialize(JsonEvent @event) =>
        JsonSerializer.Serialize(@event, EventJsonContext.Default.JsonEvent);

    private JsonEvent Redact(JsonEvent @event) => @event with
    {
        Message = @event.Message is null ? null : SecretRedactor.Redact(@event.Message, _secrets),
        Url = @event.Url is null ? null : SecretRedactor.Redact(@event.Url, _secrets),
        Target = @event.Target is null ? null : SecretRedactor.Redact(@event.Target, _secrets),
        Stage = @event.Stage is null ? null : SecretRedactor.Redact(@event.Stage, _secrets),
    };
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonEvent))]
public sealed partial class EventJsonContext : JsonSerializerContext;
