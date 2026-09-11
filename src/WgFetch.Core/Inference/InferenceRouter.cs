using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WgFetch.Core.Inference;

/// <summary>
/// Honours <see cref="AiMode"/>: local by default, remote only when explicitly configured, and
/// <see cref="AiMode.Auto"/> escalates to remote only if configured and local fails or exceeds a
/// timeout (docs/REQUIREMENTS.md, "AI execution modes"). Under remote mode, every request is logged
/// prominently — page content is leaving the box, and to which endpoint
/// (docs/REQUIREMENTS.md, "Privacy").
/// </summary>
public sealed class InferenceRouter : IInferenceRouter
{
    private readonly ITextGenerator _local;
    private readonly ITextGenerator? _remote;
    private readonly AiMode _mode;
    private readonly TimeSpan _localTimeout;
    private readonly string? _remoteEndpointForLogging;
    private readonly ILogger _logger;

    public InferenceRouter(
        ITextGenerator local,
        ITextGenerator? remote,
        AiMode mode,
        TimeSpan? localTimeout = null,
        string? remoteEndpointForLogging = null,
        ILogger? logger = null)
    {
        _local = local;
        _remote = remote;
        _mode = mode;
        _localTimeout = localTimeout ?? TimeSpan.FromSeconds(30);
        _remoteEndpointForLogging = remoteEndpointForLogging;
        _logger = logger ?? NullLogger.Instance;

        if (mode == AiMode.Remote && remote is null)
        {
            throw new InvalidOperationException("--ai-mode remote requires --ai-endpoint/--ai-model/--ai-key to be configured.");
        }
    }

    public async Task<string> GenerateAsync(string prompt, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        if (_mode == AiMode.Remote)
        {
            return await GenerateRemoteAsync(prompt, onProgress, cancellationToken).ConfigureAwait(false);
        }

        if (_mode == AiMode.Local || _remote is null)
        {
            using var gate = await LlmConcurrencyGate.AcquireAsync(AiMode.Local, cancellationToken).ConfigureAwait(false);
            return await _local.GenerateAsync(prompt, onProgress, cancellationToken).ConfigureAwait(false);
        }

        // Auto: prefer local, escalate to remote only on failure or timeout.
        using (var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            localCts.CancelAfter(_localTimeout);
            try
            {
                using var gate = await LlmConcurrencyGate.AcquireAsync(AiMode.Local, cancellationToken).ConfigureAwait(false);
                return await _local.GenerateAsync(prompt, onProgress, localCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Local inference exceeded {Timeout}; escalating to remote endpoint under --ai-mode auto.", _localTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Local inference failed ({Message}); escalating to remote endpoint under --ai-mode auto.", ex.Message);
            }
        }

        return await GenerateRemoteAsync(prompt, onProgress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GenerateRemoteAsync(string prompt, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        if (_remote is null)
        {
            throw new InvalidOperationException("Remote inference was requested but no remote text generator is configured.");
        }

        _logger.LogWarning(
            "Remote inference: page/prompt content is leaving this machine and being sent to {Endpoint}.",
            _remoteEndpointForLogging ?? "the configured remote endpoint");

        using var gate = await LlmConcurrencyGate.AcquireAsync(AiMode.Remote, cancellationToken).ConfigureAwait(false);
        return await _remote.GenerateAsync(prompt, onProgress, cancellationToken).ConfigureAwait(false);
    }
}
