using YamlDotNet.Core;

namespace WgFetch.Core.Targets;

/// <summary>Indicates that a <c>targets.yaml</c> document could not be parsed or validated.</summary>
public sealed class TargetsFileException : Exception
{
    /// <summary>Fallback reason code used when validation failed without a more specific code.</summary>
    public const string InvalidDocumentReasonCode = "invalid-document";

    /// <summary>Initializes a parse or validation error for an optional source <paramref name="path"/>.</summary>
    public TargetsFileException(string? path, Exception innerException)
        : base(BuildMessage(path, innerException), innerException)
    {
        Path = path;
    }

    /// <summary>Initializes a schema-validation error with a stable sanitized reason code.</summary>
    public TargetsFileException(string? path, Exception innerException, string reasonCode)
        : base(BuildMessage(path, innerException, reasonCode), innerException)
    {
        Path = path;
        ReasonCode = reasonCode;
    }

    /// <summary>Gets the source path, when the YAML was loaded from a file.</summary>
    public string? Path { get; }

    /// <summary>Gets a stable sanitized reason code for schema-validation failures.</summary>
    public string? ReasonCode { get; }

    private static string BuildMessage(string? path, Exception innerException, string? reasonCode = null)
    {
        string prefix = $"failed to parse targets file{(path is null ? string.Empty : $" '{path}'")}: ";
        return innerException is YamlException yamlException
            ? $"{prefix}invalid YAML at line {yamlException.Start.Line + 1}, column {yamlException.Start.Column + 1}{RenderReasonCodeSuffix(reasonCode)}."
            : $"{prefix}invalid targets document (reason: {reasonCode ?? InvalidDocumentReasonCode}).";
    }

    private static string RenderReasonCodeSuffix(string? reasonCode) =>
        reasonCode is null ? string.Empty : $" (reason: {reasonCode})";
}
