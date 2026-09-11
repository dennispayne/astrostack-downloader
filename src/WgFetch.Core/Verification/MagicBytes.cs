using System.Text;

namespace WgFetch.Core.Verification;

/// <summary>Installer container formats recognised by magic-byte inspection.</summary>
public enum InstallerFormat
{
    Unknown,
    PortableExecutable,
    InnoSetup,
    NullsoftInstaller,
    WindowsInstaller,
    Zip,
    SevenZip,
    Cabinet,
    Msix,
}

/// <summary>
/// Purely mechanical magic-byte classification of the leading chunk of a download. No model output
/// participates (docs/REQUIREMENTS.md, "The central safety invariant", check 5).
/// </summary>
public static class MagicBytes
{
    /// <summary>Number of leading bytes required for a confident classification.</summary>
    public const int InspectionChunkSize = 8 * 1024;

    private static ReadOnlySpan<byte> OleCompound => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static ReadOnlySpan<byte> Zip => [0x50, 0x4B, 0x03, 0x04];

    private static ReadOnlySpan<byte> ZipEmpty => [0x50, 0x4B, 0x05, 0x06];

    private static ReadOnlySpan<byte> SevenZip => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    private static ReadOnlySpan<byte> Cabinet => [0x4D, 0x53, 0x43, 0x46];

    private static readonly string[] HtmlMarkers =
    [
        "<!doctype html", "<html", "<head", "<body", "<?xml", "<script", "<!-- ",
    ];

    /// <summary>Classifies the leading bytes of a payload.</summary>
    public static InstallerFormat Classify(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 4)
        {
            return InstallerFormat.Unknown;
        }

        if (chunk.StartsWith(OleCompound))
        {
            return InstallerFormat.WindowsInstaller;
        }

        if (chunk.StartsWith(SevenZip))
        {
            return InstallerFormat.SevenZip;
        }

        if (chunk.StartsWith(Cabinet))
        {
            return InstallerFormat.Cabinet;
        }

        if (chunk.StartsWith(Zip) || chunk.StartsWith(ZipEmpty))
        {
            return ContainsAscii(chunk, "AppxManifest.xml") || ContainsAscii(chunk, "AppxMetadata")
                ? InstallerFormat.Msix
                : InstallerFormat.Zip;
        }

        if (chunk[0] == (byte)'M' && chunk[1] == (byte)'Z')
        {
            if (ContainsAscii(chunk, "Inno Setup") || ContainsAscii(chunk, "JR.Inno.Setup"))
            {
                return InstallerFormat.InnoSetup;
            }

            if (ContainsAscii(chunk, "NullsoftInst") || ContainsAscii(chunk, "Nullsoft Install"))
            {
                return InstallerFormat.NullsoftInstaller;
            }

            return InstallerFormat.PortableExecutable;
        }

        return InstallerFormat.Unknown;
    }

    /// <summary>True when the leading bytes look like a web page — a login, error or interstitial.</summary>
    public static bool LooksLikeMarkup(ReadOnlySpan<byte> chunk)
    {
        var length = Math.Min(chunk.Length, 1024);
        if (length == 0)
        {
            return false;
        }

        // Skip a UTF-8 BOM and leading whitespace before sniffing.
        var start = 0;
        if (length >= 3 && chunk[0] == 0xEF && chunk[1] == 0xBB && chunk[2] == 0xBF)
        {
            start = 3;
        }

        while (start < length && (chunk[start] is 0x20 or 0x09 or 0x0A or 0x0D))
        {
            start++;
        }

        if (start >= length)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(chunk[start..length]).ToLowerInvariant();
        foreach (var marker in HtmlMarkers)
        {
            if (text.StartsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the format is an accepted installer container.</summary>
    public static bool IsAcceptedInstaller(InstallerFormat format) => format is not InstallerFormat.Unknown;

    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, string needle)
    {
        var probe = Encoding.ASCII.GetBytes(needle);
        return haystack.IndexOf(probe) >= 0;
    }
}
