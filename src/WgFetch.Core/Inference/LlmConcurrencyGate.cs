namespace WgFetch.Core.Inference;

/// <summary>
/// Global concurrency gate for generative inference. Hardcoded to 1 concurrent generation with a
/// single model instance; widens to 4 only under <see cref="AiMode.Remote"/>
/// (docs/REQUIREMENTS.md, "Parallelism": "No flag for LLM concurrency — hardcode 1... Under
/// <c>--ai-mode remote</c> the funnel may widen to ~4, and only then."). There is deliberately no
/// public knob to change these numbers.
/// </summary>
public sealed class LlmConcurrencyGate
{
    private const int LocalConcurrency = 1;
    private const int RemoteConcurrency = 4;

    private static readonly SemaphoreSlim LocalGate = new(LocalConcurrency, LocalConcurrency);
    private static readonly SemaphoreSlim RemoteGate = new(RemoteConcurrency, RemoteConcurrency);

    /// <summary>Acquires the concurrency slot appropriate to <paramref name="mode"/>; dispose the result to release it.</summary>
    public static async Task<IDisposable> AcquireAsync(AiMode mode, CancellationToken cancellationToken)
    {
        var gate = mode == AiMode.Remote ? RemoteGate : LocalGate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _released;

        public Releaser(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _gate.Release();
            }
        }
    }
}
