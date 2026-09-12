namespace WgFetch.Core.Targets;

/// <summary>
/// A <c>targets.yaml</c> that exists but cannot be read or parsed. Commands translate this into a
/// clean stderr message and a usage exit code instead of letting a raw parser exception escape
/// (docs/REQUIREMENTS.md, "Testing": malformed input must "never crash or hang").
/// </summary>
public sealed class TargetsFileException : Exception
{
    public TargetsFileException()
    {
    }

    public TargetsFileException(string message)
        : base(message)
    {
    }

    public TargetsFileException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The offending file, when known.</summary>
    public string? FilePath { get; init; }
}
