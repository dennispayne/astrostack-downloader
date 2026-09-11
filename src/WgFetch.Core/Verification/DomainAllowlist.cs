namespace WgFetch.Core.Verification;

/// <summary>
/// Vendor domains a package's installer may legitimately come from. The allowlist is seeded by recipe
/// or established on first human-confirmed resolution — it is never inferred by the model at fetch
/// time (docs/REQUIREMENTS.md, verification gate check 1).
/// </summary>
public sealed class DomainAllowlist
{
    private readonly HashSet<string> _domains;

    public DomainAllowlist(IEnumerable<string> domains)
    {
        _domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domains)
        {
            var normalized = Normalize(domain);
            if (normalized.Length > 0)
            {
                _domains.Add(normalized);
            }
        }
    }

    public static DomainAllowlist Empty { get; } = new(Array.Empty<string>());

    public IReadOnlyCollection<string> Domains => _domains;

    public bool IsEmpty => _domains.Count == 0;

    /// <summary>True when the host is an allowlisted domain or a subdomain of one.</summary>
    public bool Allows(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Allows(uri.Host);
    }

    public bool Allows(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || _domains.Count == 0)
        {
            return false;
        }

        var normalized = Normalize(host);
        if (_domains.Contains(normalized))
        {
            return true;
        }

        foreach (var domain in _domains)
        {
            if (normalized.Length > domain.Length &&
                normalized.EndsWith(domain, StringComparison.OrdinalIgnoreCase) &&
                normalized[normalized.Length - domain.Length - 1] == '.')
            {
                return true;
            }
        }

        return false;
    }

    public DomainAllowlist With(params string[] additional) => new(_domains.Concat(additional));

    private static string Normalize(string domain)
    {
        var value = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.StartsWith("https://", StringComparison.Ordinal) ||
            value.StartsWith("http://", StringComparison.Ordinal))
        {
            value = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value;
        }

        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }
}
