using System.Text;
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
        string prefix = $"failed to parse targets file{(path is null ? string.Empty : $" '{EscapeForSingleLineDisplay(path)}'")}: ";
        return innerException is YamlException yamlException
            ? $"{prefix}invalid YAML at line {yamlException.Start.Line}, column {yamlException.Start.Column}{RenderReasonCodeSuffix(reasonCode ?? InvalidDocumentReasonCode)}."
            : $"{prefix}invalid targets document (reason: {reasonCode ?? InvalidDocumentReasonCode}).";
    }

    private static string RenderReasonCodeSuffix(string? reasonCode) =>
        reasonCode is null ? string.Empty : $" (reason: {reasonCode})";

    /// <summary>
    /// Escapes control characters (notably CR/LF) in <paramref name="value"/> so an attacker- or
    /// mistake-controlled path cannot break the single-line stderr/JSON error contract. This is a
    /// bespoke display-only escaping (C-style <c>\r</c>/<c>\n</c>/<c>\t</c> plus <c>\xNN</c> hex for
    /// any other control character), not a standard format such as JSON string escaping; it exists
    /// purely to keep the rendered message on one line and is not meant to be un-escaped.
    /// </summary>
    private static string EscapeForSingleLineDisplay(string value)
    {
        StringBuilder? builder = null;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsControl(c))
            {
                builder?.Append(c);
                continue;
            }

            builder ??= new StringBuilder(value.Length + 8).Append(value, 0, i);
            builder.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => $"\\x{(int)c:x2}",
            });
        }

        return builder?.ToString() ?? value;
    }
}
