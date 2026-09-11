using System.Text;
using WgFetch.Core.Tests.Support;
using WgFetch.Core.Verification;

namespace WgFetch.Core.Tests.Verification;

/// <summary>
/// Magic-byte classification is check 5 of the gate and is purely mechanical. A login page, error page
/// or interstitial must never classify as an installer (docs/REQUIREMENTS.md, "The central safety
/// invariant").
/// </summary>
public sealed class MagicBytesTests
{
    [Fact]
    public void A_chunk_shorter_than_four_bytes_is_never_classified()
    {
        Assert.Equal(InstallerFormat.Unknown, MagicBytes.Classify([0x4D, 0x5A, 0x90]));
        Assert.Equal(InstallerFormat.Unknown, MagicBytes.Classify(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Exactly_four_bytes_are_enough_for_a_portable_executable()
    {
        Assert.Equal(InstallerFormat.PortableExecutable, MagicBytes.Classify([0x4D, 0x5A, 0x90, 0x00]));
    }

    [Theory]
    [InlineData("Inno Setup", InstallerFormat.InnoSetup)]
    [InlineData("JR.Inno.Setup", InstallerFormat.InnoSetup)]
    [InlineData("NullsoftInst", InstallerFormat.NullsoftInstaller)]
    [InlineData("Nullsoft Install", InstallerFormat.NullsoftInstaller)]
    public void Each_installer_marker_is_recognised_on_its_own(string marker, InstallerFormat expected)
    {
        var bytes = new byte[4096];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 512);

        Assert.Equal(expected, MagicBytes.Classify(bytes));
    }

    [Fact]
    public void A_bare_pe_without_a_setup_marker_stays_a_portable_executable()
    {
        Assert.Equal(InstallerFormat.PortableExecutable, MagicBytes.Classify(FakeInstaller.PortableExecutable()));
    }

    [Theory]
    [InlineData("AppxManifest.xml")]
    [InlineData("AppxMetadata")]
    public void A_zip_carrying_an_appx_marker_is_an_msix(string marker)
    {
        var bytes = FakeInstaller.Zip();
        Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 64);

        Assert.Equal(InstallerFormat.Msix, MagicBytes.Classify(bytes));
    }

    [Fact]
    public void A_zip_without_an_appx_marker_stays_a_zip()
    {
        Assert.Equal(InstallerFormat.Zip, MagicBytes.Classify(FakeInstaller.Zip()));
    }

    [Fact]
    public void An_empty_zip_record_is_still_a_zip()
    {
        Assert.Equal(InstallerFormat.Zip, MagicBytes.Classify([0x50, 0x4B, 0x05, 0x06, 0x00, 0x00]));
    }

    [Fact]
    public void Other_known_containers_are_classified_by_their_signatures()
    {
        Assert.Equal(InstallerFormat.WindowsInstaller, MagicBytes.Classify(FakeInstaller.Msi()));
        Assert.Equal(InstallerFormat.SevenZip, MagicBytes.Classify([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]));
        Assert.Equal(InstallerFormat.Cabinet, MagicBytes.Classify([0x4D, 0x53, 0x43, 0x46, 0x00, 0x00]));
        Assert.Equal(InstallerFormat.Unknown, MagicBytes.Classify([0x00, 0x01, 0x02, 0x03]));
    }

    [Fact]
    public void Only_the_unknown_format_is_refused_as_an_installer()
    {
        Assert.False(MagicBytes.IsAcceptedInstaller(InstallerFormat.Unknown));
        foreach (var format in Enum.GetValues<InstallerFormat>().Where(f => f != InstallerFormat.Unknown))
        {
            Assert.True(MagicBytes.IsAcceptedInstaller(format));
        }
    }

    [Theory]
    [InlineData("<!doctype html><html>")]
    [InlineData("<html><body>sign in</body></html>")]
    [InlineData("<head>")]
    [InlineData("<body>")]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("<script>location='/login'</script>")]
    [InlineData("<!-- interstitial -->")]
    public void Markup_is_detected_from_its_leading_bytes(string markup)
    {
        Assert.True(MagicBytes.LooksLikeMarkup(Encoding.ASCII.GetBytes(markup)));
    }

    [Fact]
    public void Markup_is_detected_behind_a_utf8_bom_and_leading_whitespace()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.ASCII.GetBytes(" \t\r\n<html>"))
            .ToArray();

        Assert.True(MagicBytes.LooksLikeMarkup(withBom));
    }

    [Fact]
    public void A_partial_bom_is_not_skipped_and_does_not_read_past_the_chunk()
    {
        // Two of the three BOM bytes: the sniffing must not treat them as a BOM, and must not fault.
        Assert.False(MagicBytes.LooksLikeMarkup([0xEF, 0xBB]));
        Assert.False(MagicBytes.LooksLikeMarkup([0xEF, 0xBB, 0x00]));
        Assert.False(MagicBytes.LooksLikeMarkup([0xEF, 0x00, 0xBF]));
        Assert.False(MagicBytes.LooksLikeMarkup([0x00, 0xBB, 0xBF]));
    }

    [Fact]
    public void Whitespace_only_and_empty_payloads_are_not_markup()
    {
        Assert.False(MagicBytes.LooksLikeMarkup(ReadOnlySpan<byte>.Empty));
        Assert.False(MagicBytes.LooksLikeMarkup(Encoding.ASCII.GetBytes("    ")));
        Assert.False(MagicBytes.LooksLikeMarkup([0xEF, 0xBB, 0xBF]));
    }

    [Fact]
    public void A_markup_marker_appearing_later_in_the_payload_is_not_markup()
    {
        var bytes = FakeInstaller.PortableExecutable();
        Encoding.ASCII.GetBytes("<html>").CopyTo(bytes, 4096);

        Assert.False(MagicBytes.LooksLikeMarkup(bytes));
    }

    [Fact]
    public void An_installer_marker_immediately_after_the_pe_header_still_counts()
    {
        var bytes = new byte[1024];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        Encoding.ASCII.GetBytes("Inno Setup").CopyTo(bytes, 2);

        Assert.Equal(InstallerFormat.InnoSetup, MagicBytes.Classify(bytes));
    }
}
