namespace WgFetch.Core.Inference;

/// <summary>Routes generation requests to the local or remote tier according to <see cref="AiMode"/>.</summary>
public interface IInferenceRouter
{
    public Task<string> GenerateAsync(string prompt, Action<string>? onProgress, CancellationToken cancellationToken);
}
