namespace WgFetch.Core.Cli;

/// <summary>
/// Sanitizes human-facing terminal text so user-controlled values cannot inject control/escape
/// sequences into plain output.
/// </summary>
internal static class TerminalTextSanitizer
{
    internal static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        char[]? sanitized = null;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsControl(value[i]))
            {
                continue;
            }

            sanitized ??= value.ToCharArray();
            sanitized[i] = '?';
        }

        return sanitized is null ? value : new string(sanitized);
    }
}
