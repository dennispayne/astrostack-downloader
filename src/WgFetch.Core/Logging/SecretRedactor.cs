using System.Text.RegularExpressions;

namespace WgFetch.Core.Logging;

/// <summary>
/// Redacts API keys, tokens and credentials from every rendered string. A known secret value must
/// never appear in a log at any verbosity, in <c>provenance.json</c>, in <c>--json</c> output, or in
/// a diagnostics bundle (docs/REQUIREMENTS.md, "Privacy").
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "[REDACTED]";

    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apikey", "api_key", "token", "access_token", "auth", "authorization", "signature",
        "sig", "password", "secret", "client_secret", "x-api-key", "code",
    };

    private static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "aiKey", "searchKey", "githubToken", "apiKey", "token", "password", "secret", "authorization",
    };

    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"sk-[A-Za-z0-9\-_]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiKeyPattern();

    [GeneratedRegex(@"(?i)\b(bearer)\s+[A-Za-z0-9\-\._~\+/=]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    /// <summary>True when a configuration or provenance field name holds a secret.</summary>
    public static bool IsSensitiveFieldName(string name) => SensitiveFieldNames.Contains(name);

    /// <summary>Redacts well-known secret shapes and any explicitly registered secret values.</summary>
    public static string Redact(string? text, IEnumerable<string>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;

        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                // No minimum length here: a configured credential must never appear in output even
                // when it is short, which can occasionally over-redact an unrelated short substring
                // that happens to match it (docs/REQUIREMENTS.md, "Privacy").
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    result = result.Replace(secret, Placeholder, StringComparison.Ordinal);
                    result = result.Replace(Uri.EscapeDataString(secret), Placeholder, StringComparison.Ordinal);
                }
            }
        }

        result = GitHubTokenPattern().Replace(result, Placeholder);
        result = OpenAiKeyPattern().Replace(result, Placeholder);
        result = BearerPattern().Replace(result, $"$1 {Placeholder}");
        result = RedactUrlsInText(result);
        return result;
    }

    /// <summary>Strips credentials and sensitive query parameters from a URL.</summary>
    public static string RedactUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return RedactUrl(uri.ToString());
    }

    public static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return url;
        }

        var builder = new UriBuilder(uri);
        if (!string.IsNullOrEmpty(builder.UserName) || !string.IsNullOrEmpty(builder.Password))
        {
            builder.UserName = Placeholder;
            builder.Password = string.Empty;
        }

        var query = uri.Query;
        if (query.Length > 1)
        {
            var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var eq = parts[i].IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var name = Uri.UnescapeDataString(parts[i][..eq]);
                if (SensitiveQueryKeys.Contains(name))
                {
                    parts[i] = parts[i][..eq] + "=" + Placeholder;
                }
            }

            builder.Query = string.Join('&', parts);
        }

        var result = builder.Uri.ToString();

        // UriBuilder percent-encodes the placeholder inside userinfo; normalise it back for readability.
        return result.Replace("%5BREDACTED%5D", Placeholder, StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactUrlsInText(string text)
    {
        var result = text;
        foreach (Match match in UrlPattern().Matches(text))
        {
            var redacted = RedactUrl(match.Value);
            if (!string.Equals(redacted, match.Value, StringComparison.Ordinal))
            {
                result = result.Replace(match.Value, redacted, StringComparison.Ordinal);
            }
        }

        return result;
    }
}
