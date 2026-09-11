using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgFetch.Core.Logging;

namespace WgFetch.Core.Inference;

/// <summary>Chat message shape for the OpenAI-compatible request body, source-generated for AOT.</summary>
internal sealed record ChatMessage(string Role, string Content);

/// <summary>Chat completion request body shape, source-generated for AOT.</summary>
internal sealed record ChatCompletionRequest(string Model, ChatMessage[] Messages, bool Stream);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChatCompletionRequest))]
internal sealed partial class ChatCompletionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// A generic OpenAI-compatible chat/completions <see cref="ITextGenerator"/>
/// (<c>--ai-endpoint/--ai-model/--ai-key</c>, docs/REQUIREMENTS.md, "AI execution modes"). The API
/// key is never logged: it is passed only as a bearer header and any exception message is redacted
/// before logging.
/// </summary>
public sealed class RemoteOpenAiTextGenerator : ITextGenerator
{
    private readonly IOpenAiChatClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly ILogger _logger;

    public RemoteOpenAiTextGenerator(
        IOpenAiChatClient client,
        Uri endpoint,
        string model,
        string? apiKey,
        ILogger? logger = null)
    {
        _client = client;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<string> GenerateAsync(string prompt, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        var requestBody = JsonSerializer.Serialize(
            new ChatCompletionRequest(_model, [new ChatMessage("user", prompt)], false),
            ChatCompletionJsonContext.Default.ChatCompletionRequest);

        string responseJson;
        try
        {
            responseJson = await _client
                .PostChatCompletionAsync(_endpoint, requestBody, _apiKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The key must never leak into logs even indirectly via an exception message that
            // happened to echo request details.
            throw new InvalidOperationException($"Remote inference request failed: {SecretRedactor.Redact(ex.Message, _apiKey is null ? null : [_apiKey])}", ex);
        }

        using var document = JsonDocument.Parse(responseJson);
        var text = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        onProgress?.Invoke(text);
        return text;
    }
}
