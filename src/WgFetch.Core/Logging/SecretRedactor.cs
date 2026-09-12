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
        "key", "apikey", "api_key", "api-key", "token", "access_token", "auth", "authorization", "signature",
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
        var secrets = (knownSecrets ?? [])
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .ToArray();

        if (secrets.Length > 0)
        {
            // No minimum length here: a configured credential must never appear in output even when
            // it is short, which can occasionally over-redact an unrelated short substring that
            // happens to match it (docs/REQUIREMENTS.md, "Privacy").
            result = RedactKnownSecrets(result, secrets);
        }

        result = GitHubTokenPattern().Replace(result, Placeholder);
        result = OpenAiKeyPattern().Replace(result, Placeholder);
        result = BearerPattern().Replace(result, $"$1 {Placeholder}");
        result = RedactUrlsInText(result, secrets);
        return result;
    }

    /// <summary>Strips credentials and sensitive query parameters from a URL.</summary>
    public static string RedactUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return RedactUrl(uri.ToString());
    }

    public static string RedactUrl(string url) => RedactUrl(url, knownSecrets: null);

    /// <summary>
    /// Strips credentials and sensitive query parameters from a URL, additionally redacting any
    /// query-string value that decodes (percent- or form-encoding, i.e. <c>+</c> for space) to one of
    /// <paramref name="knownSecrets"/>. A configured credential embedded in an endpoint URL must never
    /// survive redaction merely because it was encoded differently than <see cref="Uri.EscapeDataString"/>
    /// would produce (docs/REQUIREMENTS.md, "Privacy").
    /// </summary>
    public static string RedactUrl(string url, IEnumerable<string>? knownSecrets)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return url;
        }

        var secrets = (knownSecrets ?? [])
            .Where(secret => !string.IsNullOrEmpty(secret))
            .ToArray();

        var builder = new UriBuilder(uri);
        if (!string.IsNullOrEmpty(builder.UserName) || !string.IsNullOrEmpty(builder.Password))
        {
            builder.UserName = Placeholder;
            builder.Password = string.Empty;
        }

        var query = uri.Query;
        if (query.Length > 1)
        {
            builder.Query = RedactParameters(query.TrimStart('?'), secrets);
        }

        var rawFragment = uri.Fragment;
        if (rawFragment.Length > 1)
        {
            // OAuth-style responses carry credentials in the fragment (for example
            // "#access_token=..."), which never reaches the query pass, so the same sensitive-key
            // handling is applied here (docs/REQUIREMENTS.md, "Privacy").
            // Some callbacks render the fragment as "#?token=..."; drop that separator too so the
            // parameter names are matched rather than treated as part of the first key.
            var fragmentBody = rawFragment.TrimStart('#').TrimStart('?');
            var redactedFragment = RedactParameters(fragmentBody, secrets);
            if (!string.Equals(redactedFragment, fragmentBody, StringComparison.Ordinal))
            {
                builder.Fragment = redactedFragment;
            }
        }

        if (secrets.Length > 0)
        {
            var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            var decodedPath = DecodeUriComponent(escapedPath);
            if (ContainsSecret(decodedPath, secrets))
            {
                builder.Path = RedactKnownSecrets(decodedPath, secrets);
            }

            var escapedFragment = uri.GetComponents(UriComponents.Fragment, UriFormat.UriEscaped);
            var decodedFragment = DecodeUriComponent(escapedFragment);
            if (ContainsSecret(decodedFragment, secrets))
            {
                builder.Fragment = RedactKnownSecrets(decodedFragment, secrets);
            }
        }

        var result = builder.Uri.ToString();

        // UriBuilder percent-encodes the placeholder inside userinfo; normalise it back for readability.
        result = result.Replace("%5BREDACTED%5D", Placeholder, StringComparison.OrdinalIgnoreCase);
        if (secrets.Length > 0)
        {
            result = RedactKnownSecrets(result, secrets);
        }

        // A token-shaped credential (GitHub PAT, OpenAI-style key, bearer token) may appear in a
        // URL's path or fragment even when it was never registered as a known secret; callers such
        // as InstallerDownloader use RedactUrl directly, so the well-known patterns must also be
        // applied here rather than only in Redact (docs/REQUIREMENTS.md, "Privacy").
        result = GitHubTokenPattern().Replace(result, Placeholder);
        result = OpenAiKeyPattern().Replace(result, Placeholder);
        result = BearerPattern().Replace(result, $"$1 {Placeholder}");
        return result;
    }

    /// <summary>
    /// Redacts an <c>&amp;</c>-separated parameter list (a URL query or an OAuth-style fragment),
    /// replacing values of well-known sensitive keys and any component holding a known secret.
    /// </summary>
    private static string RedactParameters(string rawParameters, IReadOnlyCollection<string> secrets)
    {
        var parts = rawParameters.Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq < 0)
            {
                // A valueless component carries no name to preserve, so replace it wholesale: the
                // secret may be encoded in a form that a textual replacement would not match.
                if (ContainsSecret(DecodeQueryComponent(parts[i]), secrets))
                {
                    parts[i] = Placeholder;
                }

                continue;
            }

            var rawName = parts[i][..eq];
            var rawValue = parts[i][(eq + 1)..];
            var name = DecodeQueryComponent(rawName);
            var value = DecodeQueryComponent(rawValue);
            var secretInName = ContainsSecret(name, secrets);
            if (SensitiveQueryKeys.Contains(name) ||
                secretInName ||
                ContainsSecret(value, secrets))
            {
                parts[i] = (secretInName ? Placeholder : rawName) + "=" + Placeholder;
            }
        }

        return string.Join('&', parts);
    }

    /// <summary>Decodes a query-string component using both percent-encoding and form-encoding (<c>+</c> for space).</summary>
    private static string DecodeQueryComponent(string rawValue)
    {
        var withSpaces = rawValue.Replace('+', ' ');
        try
        {
            return Uri.UnescapeDataString(withSpaces);
        }
        catch (FormatException)
        {
            return withSpaces;
        }
    }

    private static string DecodeUriComponent(string rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return rawValue;
        }

        try
        {
            return Uri.UnescapeDataString(rawValue);
        }
        catch (FormatException)
        {
            return rawValue;
        }
    }

    private static string RedactKnownSecrets(string text, IReadOnlyCollection<string> secrets)
    {
        var result = text;
        foreach (var secret in secrets)
        {
            result = result.Replace(secret, Placeholder, StringComparison.Ordinal);
            var escaped = Uri.EscapeDataString(secret);

            // Percent-encoding hex digits are case-insensitive (RFC 3986), but callers outside
            // HTTP URLs (e.g. arbitrary log text) may render a secret using either casing, so a
            // known credential such as "a/b" rendered as "a%2fb" must still be caught here.
            result = result.Replace(escaped, Placeholder, StringComparison.OrdinalIgnoreCase);
            var formEncoded = escaped.Replace("%20", "+", StringComparison.Ordinal);
            if (!string.Equals(formEncoded, escaped, StringComparison.Ordinal))
            {
                result = result.Replace(formEncoded, Placeholder, StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    private static bool ContainsSecret(string? value, IReadOnlyCollection<string> secrets)
    {
        if (string.IsNullOrEmpty(value) || secrets.Count == 0)
        {
            return false;
        }

        foreach (var secret in secrets)
        {
            if (value.Contains(secret, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string RedactUrlsInText(string text, IEnumerable<string>? knownSecrets)
    {
        var result = text;
        foreach (Match match in UrlPattern().Matches(text))
        {
            var redacted = RedactUrl(match.Value, knownSecrets);
            if (!string.Equals(redacted, match.Value, StringComparison.Ordinal))
            {
                result = result.Replace(match.Value, redacted, StringComparison.Ordinal);
            }
        }

        return result;
    }
}
