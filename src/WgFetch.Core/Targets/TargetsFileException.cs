using YamlDotNet.Core;

namespace WgFetch.Core.Targets;

/// <summary>Indicates that a <c>targets.yaml</c> document could not be parsed or validated.</summary>
public sealed class TargetsFileException : Exception
{
    /// <summary>Initializes a parse or validation error for an optional source <paramref name="path"/>.</summary>
    public TargetsFileException(string? path, Exception innerException)
        : base(BuildMessage(path, innerException), innerException)
    {
        Path = path;
    }

    /// <summary>Gets the source path, when the YAML was loaded from a file.</summary>
    public string? Path { get; }

    private static string BuildMessage(string? path, Exception innerException)
    {
        string prefix = $"failed to parse targets file{(path is null ? string.Empty : $" '{path}'")}: ";
        return innerException is YamlException yamlException
            ? $"{prefix}invalid YAML at line {yamlException.Start.Line}, column {yamlException.Start.Column}."
            : $"{prefix}invalid targets document.";
    }
}
