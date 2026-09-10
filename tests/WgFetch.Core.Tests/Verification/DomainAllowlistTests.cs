using WgFetch.Core.Verification;

namespace WgFetch.Core.Tests.Verification;

/// <summary>
/// Check 1 of the gate: the host must be an allowlisted vendor domain. Suffix matching must not let a
/// lookalike host through (docs/REQUIREMENTS.md, verification gate check 1).
/// </summary>
public sealed class DomainAllowlistTests
{
    private static readonly DomainAllowlist Allowlist = new(["nighttime-imaging.eu", "github.com"]);

    [Fact]
    public void An_exact_host_and_its_subdomains_are_allowed()
    {
        Assert.True(Allowlist.Allows("nighttime-imaging.eu"));
        Assert.True(Allowlist.Allows("downloads.nighttime-imaging.eu"));
        Assert.True(Allowlist.Allows(new Uri("https://release.github.com/nina/NINASetup.exe")));
    }

    [Theory]
    [InlineData("evil-nighttime-imaging.eu")]
    [InlineData("nighttime-imaging.eu.example.com")]
    [InlineData("notgithub.com")]
    [InlineData("github.com.attacker.test")]
    public void A_lookalike_host_is_refused(string host)
    {
        Assert.False(Allowlist.Allows(host));
    }

    [Fact]
    public void A_host_shorter_than_or_equal_to_the_domain_cannot_match_by_suffix()
    {
        Assert.False(Allowlist.Allows("hub.com"));
        Assert.False(Allowlist.Allows("ithub.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_host_is_refused(string? host)
    {
        Assert.False(Allowlist.Allows(host));
    }

    [Fact]
    public void An_empty_allowlist_allows_nothing()
    {
        Assert.True(DomainAllowlist.Empty.IsEmpty);
        Assert.False(DomainAllowlist.Empty.Allows("github.com"));
        Assert.Empty(DomainAllowlist.Empty.Domains);
    }

    [Theory]
    [InlineData("GitHub.com")]
    [InlineData("  github.com  ")]
    [InlineData("github.com.")]
    [InlineData("*.github.com")]
    [InlineData("https://github.com/some/path")]
    [InlineData("http://github.com")]
    public void Domain_entries_are_normalised_when_the_allowlist_is_built(string entry)
    {
        var allowlist = new DomainAllowlist([entry]);

        Assert.Equal(["github.com"], allowlist.Domains);
        Assert.True(allowlist.Allows("release.github.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_entries_are_dropped_rather_than_matching_everything(string entry)
    {
        var allowlist = new DomainAllowlist([entry]);

        Assert.True(allowlist.IsEmpty);
        Assert.False(allowlist.Allows("github.com"));
    }

    [Fact]
    public void Hosts_are_normalised_before_matching()
    {
        Assert.True(Allowlist.Allows("GITHUB.COM"));
        Assert.True(Allowlist.Allows("Release.GitHub.com"));
        Assert.True(Allowlist.Allows("github.com."));
    }

    [Fact]
    public void With_returns_a_superset_and_leaves_the_original_untouched()
    {
        var extended = Allowlist.With("astap.sourceforge.io");

        Assert.True(extended.Allows("astap.sourceforge.io"));
        Assert.True(extended.Allows("github.com"));
        Assert.False(Allowlist.Allows("astap.sourceforge.io"));
    }

    [Fact]
    public void A_null_uri_is_rejected_loudly_rather_than_silently_allowed()
    {
        Assert.Throws<ArgumentNullException>(() => Allowlist.Allows((Uri)null!));
    }
}
