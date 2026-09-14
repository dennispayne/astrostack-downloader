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

    // Parameter/header names are short in practice; stack-allocate the scratch buffer up to this
    // length and fall back to the heap only for the rare longer name, avoiding an allocation on the
    // common path without risking stack overflow for adversarial input.
    private const int MaxStackAllocParameterNameLength = 64;

    // Stored in normalized form (lower-case, separators removed) so every spelling of a key —
    // "api-key", "api_key", "X-Api-Key" — matches a single entry (see NormalizeParameterName).
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.Ordinal)
    {
        "key", "apikey", "token", "accesstoken", "refreshtoken", "idtoken", "auth", "authorization",
        "signature", "sig", "password", "secret", "clientsecret", "xapikey", "code",
    };

    // Vendor-specific and custom credential parameters (for example "X-Amz-Signature" or
    // "custom_token") vary too widely to enumerate exactly, so a normalized name ending in one of
    // these suffixes at a word boundary (a separator or a camelCase transition in the original name,
    // checked by IsSensitiveParameterName) is treated as sensitive too. A plain substring suffix match
    // would also flag unrelated names such as "monkey" (ends in "key"), so the boundary check is
    // required (docs/REQUIREMENTS.md, "Privacy").
    private static readonly string[] SensitiveQueryKeySuffixes =
    [
        "key", "token", "secret", "signature", "password", "credential", "auth",
    ];

    private static readonly string[] SensitiveQueryFragments =
        ["credential", "signature", "token", "hmac"];

    private static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "aiKey", "searchKey", "githubToken", "apiKey", "token", "password", "secret", "authorization",
    };

    private static readonly string[] SensitiveHeaderFragments =
        ["api-key", "apikey", "authorization", "credential", "password", "token", "secret"];

    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "X-Auth", "X-Authorization", "Cookie", "Cookie2",
        "Ocp-Apim-Subscription-Key",
    };

    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"sk-[A-Za-z0-9\-_]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiKeyPattern();

    [GeneratedRegex(@"(?i)\b(bearer)\s+[A-Za-z0-9\-\._~\+/=]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>True when a configuration or provenance field name holds a secret.</summary>
    public static bool IsSensitiveFieldName(string name) => SensitiveFieldNames.Contains(name);

    /// <summary>True when an HTTP header can carry credentials and must not cross authorities.</summary>
    public static bool IsSensitiveHeaderName(string name) =>
        SensitiveHeaderNames.Contains(name) ||
        name.EndsWith("-key", StringComparison.OrdinalIgnoreCase) ||
        SensitiveHeaderFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

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

        // URL-aware redaction must run before the literal known-secret replace below. Otherwise a
        // short configured secret that happens to equal a sensitive query-parameter name (for
        // example a secret literally equal to "token") gets replaced first, mangling "?token=..."
        // into "?[REDACTED]=..." so the URL parser no longer recognizes the parameter as sensitive
        // and the actual secret value in it is never redacted (docs/REQUIREMENTS.md, "Privacy").
        result = RedactUrlsInText(result, secrets);

        result = GitHubTokenPattern().Replace(result, Placeholder);
        result = OpenAiKeyPattern().Replace(result, Placeholder);
        result = BearerPattern().Replace(result, $"$1 {Placeholder}");

        if (secrets.Length > 0)
        {
            // No minimum length here: a configured credential must never appear in output even when
            // it is short, which can occasionally over-redact an unrelated short substring that
            // happens to match it (docs/REQUIREMENTS.md, "Privacy").
            result = RedactKnownSecrets(result, secrets);
        }

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
        if (IsWindowsDrivePath(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
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
            // Some callbacks render the fragment as "#?token=..."; drop that separator for matching
            // and restore it afterwards so redaction never changes the shape of the URL.
            var afterHash = rawFragment.TrimStart('#');
            var hasQueryPrefix = afterHash.StartsWith('?');
            var fragmentBody = hasQueryPrefix ? afterHash[1..] : afterHash;
            var redactedFragment = RedactParameters(fragmentBody, secrets);
            if (!string.Equals(redactedFragment, fragmentBody, StringComparison.Ordinal))
            {
                builder.Fragment = hasQueryPrefix ? "?" + redactedFragment : redactedFragment;
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

            var currentFragment = DecodeUriComponent(builder.Fragment.TrimStart('#'));
            if (ContainsSecret(currentFragment, secrets))
            {
                // Apply the known-secret replacement to the fragment already redacted above (not the
                // raw original fragment), otherwise this pass would discard the sensitive-parameter
                // redaction performed for "#access_token=..." style fragments.
                builder.Fragment = RedactKnownSecrets(currentFragment, secrets);
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
            if (IsSensitiveParameterName(name) ||
                secretInName ||
                ContainsSecret(value, secrets))
            {
                parts[i] = (secretInName ? Placeholder : rawName) + "=" + Placeholder;
            }
        }

        return string.Join('&', parts);
    }

    /// <summary>
    /// True when a query/fragment parameter name is a known sensitive key, or a vendor/custom
    /// variant of one (for example <c>X-Amz-Signature</c> or <c>custom_token</c>), after
    /// normalization.
    /// </summary>
    private static bool IsSensitiveParameterName(string name)
    {
        if (SensitiveQueryFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var normalized = NormalizeParameterName(name);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (SensitiveQueryKeys.Contains(normalized))
        {
            return true;
        }

        foreach (var suffix in SensitiveQueryKeySuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal) &&
                HasWordBoundaryBeforeSuffix(name, suffix.Length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the suffix of length <paramref name="suffixLength"/> that ends a normalized
    /// parameter name is preceded, in the original (un-normalized) <paramref name="originalName"/>,
    /// by a separator (<c>-</c>, <c>_</c>, <c>.</c>, whitespace) or a camelCase transition (a
    /// lower-case character followed by an upper-case one) — or the suffix is the entire name.
    /// Without this boundary check, an unrelated parameter that merely ends with a sensitive
    /// substring, such as "monkey" ending in "key", would be wrongly treated as sensitive.
    /// </summary>
    private static bool HasWordBoundaryBeforeSuffix(string originalName, int suffixLength)
    {
        Span<int> keptIndices = originalName.Length <= MaxStackAllocParameterNameLength ? stackalloc int[originalName.Length] : new int[originalName.Length];
        var keptCount = 0;
        for (var i = 0; i < originalName.Length; i++)
        {
            var character = originalName[i];
            if (character is '-' or '_' or '.' || char.IsWhiteSpace(character))
            {
                continue;
            }

            keptIndices[keptCount++] = i;
        }

        var suffixStartKeptIndex = keptCount - suffixLength;
        if (suffixStartKeptIndex <= 0)
        {
            return true;
        }

        var suffixStart = keptIndices[suffixStartKeptIndex];
        var previousKept = keptIndices[suffixStartKeptIndex - 1];
        if (suffixStart - previousKept > 1)
        {
            // A separator character sat between the previous kept character and the suffix.
            return true;
        }

        return char.IsUpper(originalName[suffixStart]) && !char.IsUpper(originalName[previousKept]);
    }

    /// <summary>
    /// Normalizes a parameter name for sensitive-key lookup: lower-cased with the separators that
    /// vendors interchange freely (<c>-</c>, <c>_</c>, <c>.</c>, whitespace) removed.
    /// </summary>
    private static string NormalizeParameterName(string name)
    {
        Span<char> buffer = name.Length <= MaxStackAllocParameterNameLength ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;
        foreach (var character in name)
        {
            if (character is '-' or '_' or '.' || char.IsWhiteSpace(character))
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(character);
        }

        return new string(buffer[..length]);
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

    private static bool IsWindowsDrivePath(string value) =>
        value.Length >= 3 &&
        char.IsAsciiLetter(value[0]) &&
        value[1] == ':' &&
        value[2] is '\\' or '/';

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
