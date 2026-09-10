using System.Globalization;
using System.Text;

namespace WgFetch.Core.Versioning;

/// <summary>Broad shape of a vendor version string.</summary>
public enum VersionKind
{
    /// <summary>Nothing numeric could be extracted.</summary>
    Opaque,

    /// <summary>Strict <c>major.minor.patch</c> with optional pre-release / build metadata.</summary>
    SemVer,

    /// <summary>Two or more dot-separated numeric components, e.g. <c>3.2.0.9001</c>.</summary>
    DottedNumeric,

    /// <summary>Date stamped, e.g. <c>2024.11.1</c>, <c>2024-11-01</c> or <c>20241101</c>.</summary>
    Date,

    /// <summary>Mixed alphanumeric, e.g. <c>1.2 HF3</c> or <c>4.1b7</c>.</summary>
    Mixed,
}

/// <summary>A single comparison token: numeric runs compare numerically, alphabetic runs ordinally.</summary>
public readonly record struct VersionToken(bool IsNumeric, long Number, string Text)
{
    public static VersionToken Numeric(long value) => new(true, value, value.ToString(CultureInfo.InvariantCulture));

    public static VersionToken Alpha(string text) => new(false, 0, text);

    public override string ToString() => Text;
}

/// <summary>
/// A parsed vendor version. Vendor versions are frequently not semver, so parsing never throws and
/// never assumes a shape (docs/REQUIREMENTS.md, "Version handling").
/// </summary>
public sealed class VersionValue : IEquatable<VersionValue>
{
    private VersionValue(
        string raw,
        VersionKind kind,
        IReadOnlyList<VersionToken> core,
        IReadOnlyList<VersionToken> preRelease,
        bool hasPreRelease)
    {
        Raw = raw;
        Kind = kind;
        Core = core;
        PreRelease = preRelease;
        HasPreRelease = hasPreRelease;
    }

    public string Raw { get; }

    public VersionKind Kind { get; }

    /// <summary>Tokens of the release portion of the version.</summary>
    public IReadOnlyList<VersionToken> Core { get; }

    /// <summary>Tokens of the pre-release portion, if any.</summary>
    public IReadOnlyList<VersionToken> PreRelease { get; }

    public bool HasPreRelease { get; }

    /// <summary>A canonical rendering used for stable tie-breaking and for logging.</summary>
    public string Normalized
    {
        get
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Core.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('.');
                }

                sb.Append(Core[i].Text);
            }

            if (HasPreRelease)
            {
                sb.Append('-');
                for (var i = 0; i < PreRelease.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append('.');
                    }

                    sb.Append(PreRelease[i].Text);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>Parses a vendor version string. Never throws; unparseable input becomes an opaque version.</summary>
    public static VersionValue Parse(string? raw)
    {
        raw ??= string.Empty;
        var trimmed = raw.Trim();
        var work = trimmed;

        // Strip a leading "v" / "version" prefix that vendors add inconsistently.
        if (work.StartsWith("version", StringComparison.OrdinalIgnoreCase))
        {
            work = work[7..].TrimStart(' ', '-', '_', ':');
        }
        else if (work.Length > 1 && (work[0] is 'v' or 'V') && char.IsDigit(work[1]))
        {
            work = work[1..];
        }

        // Build metadata (semver "+meta") never participates in precedence.
        var plus = work.IndexOf('+');
        if (plus >= 0)
        {
            work = work[..plus];
        }

        var (corePart, prePart, hasPre) = SplitPreRelease(work);
        var core = Tokenize(corePart);
        IReadOnlyList<VersionToken> pre = hasPre ? Tokenize(prePart) : Array.Empty<VersionToken>();

        if (core.Count == 0)
        {
            return new VersionValue(trimmed, VersionKind.Opaque, core, pre, hasPre);
        }

        return new VersionValue(trimmed, ClassifyKind(corePart, core, hasPre), core, pre, hasPre);
    }

    private static (string Core, string Pre, bool HasPre) SplitPreRelease(string work)
    {
        // A dash-separated date stamp ("2024-11-01") is a separator, not a semver pre-release tag.
        if (IsDashSeparatedNumeric(work))
        {
            return (work, string.Empty, false);
        }

        // Semver-style pre-release: the first '-' that is followed by a non-empty tag and where the
        // portion before it is purely dotted-numeric.
        var dash = work.IndexOf('-');
        if (dash > 0 && dash < work.Length - 1)
        {
            var head = work[..dash];
            if (IsDottedNumeric(head) && !IsDottedNumeric(work[(dash + 1)..]))
            {
                return (head, work[(dash + 1)..], true);
            }
        }

        return (work, string.Empty, false);
    }

    private static bool IsDashSeparatedNumeric(string value)
    {
        var digits = false;
        foreach (var c in value)
        {
            if (char.IsDigit(c))
            {
                digits = true;
                continue;
            }

            if (c is not ('-' or '.'))
            {
                return false;
            }
        }

        return digits && value.Contains('-', StringComparison.Ordinal);
    }

    private static bool IsDottedNumeric(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsDigit(c) && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static VersionKind ClassifyKind(string corePart, IReadOnlyList<VersionToken> core, bool hasPre)
    {
        if (!core.Any(t => t.IsNumeric))
        {
            // "Nothing numeric could be extracted" — e.g. "unreleased", "latest".
            return VersionKind.Opaque;
        }

        if (LooksLikeDate(core))
        {
            return VersionKind.Date;
        }

        var allNumeric = core.All(t => t.IsNumeric);
        if (!allNumeric)
        {
            return VersionKind.Mixed;
        }

        var dotted = corePart.Count(c => c == '.') + 1 == core.Count;
        if (core.Count == 3 && dotted)
        {
            return VersionKind.SemVer;
        }

        return hasPre && dotted ? VersionKind.SemVer : VersionKind.DottedNumeric;
    }

    private static bool LooksLikeDate(IReadOnlyList<VersionToken> core)
    {
        if (core.Count == 0 || !core[0].IsNumeric)
        {
            return false;
        }

        var first = core[0].Number;
        if (core.Count == 1)
        {
            // 20241101
            if (first is >= 19900101 and <= 29991231)
            {
                var month = (first / 100) % 100;
                var day = first % 100;
                return month is >= 1 and <= 12 && day is >= 1 and <= 31;
            }

            return false;
        }

        if (first is < 1990 or > 2999)
        {
            return false;
        }

        if (core.Count < 2 || !core[1].IsNumeric || core[1].Number is < 1 or > 12)
        {
            return false;
        }

        if (core.Count >= 3 && core[2].IsNumeric && core[2].Number is < 1 or > 31)
        {
            return false;
        }

        return core.All(t => t.IsNumeric);
    }

    private static List<VersionToken> Tokenize(string value)
    {
        var tokens = new List<VersionToken>();
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < value.Length && char.IsDigit(value[i]))
                {
                    i++;
                }

                var text = value[start..i].TrimStart('0');
                var parsed = text.Length == 0
                    ? 0
                    : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue;
                tokens.Add(VersionToken.Numeric(parsed));
            }
            else if (char.IsLetter(c))
            {
                var start = i;
                while (i < value.Length && char.IsLetter(value[i]))
                {
                    i++;
                }

                tokens.Add(VersionToken.Alpha(value[start..i].ToLowerInvariant()));
            }
            else
            {
                i++;
            }
        }

        return tokens;
    }

    public bool Equals(VersionValue? other) =>
        other is not null && string.Equals(Raw, other.Raw, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as VersionValue);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Raw);

    public override string ToString() => Raw;
}
