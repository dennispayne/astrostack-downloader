using YamlDotNet.Core;

namespace WgFetch.Core.Targets;

/// <summary>Indicates that a <c>targets.yaml</c> document could not be parsed.</summary>
public sealed class TargetsFileException : Exception
{
    /// <summary>Initializes a parse error for an optional source <paramref name="path"/>.</summary>
    public TargetsFileException(string? path, YamlException innerException)
        : base(
            $"failed to parse targets file{(path is null ? string.Empty : $" '{path}'")}: " +
            $"invalid YAML at line {innerException.Start.Line}, column {innerException.Start.Column}.",
            innerException)
    {
        Path = path;
    }

    /// <summary>Gets the source path, when the YAML was loaded from a file.</summary>
    public string? Path { get; }
}
