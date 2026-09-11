using WgFetch.Core.Versioning;

namespace WgFetch.Core.Tests.Versioning;

/// <summary>
/// Vendor versions are frequently not semver, so parsing never throws and never assumes a shape
/// (docs/REQUIREMENTS.md, "Version handling"). These tests pin the classification boundaries and the
/// canonical rendering used for tie-breaking and logging.
/// </summary>
public sealed class VersionValueTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("latest", "latest")]
    public void Unparseable_input_becomes_an_opaque_version_and_keeps_its_trimmed_raw(string? raw, string expected)
    {
        var version = VersionValue.Parse(raw);

        Assert.Equal(VersionKind.Opaque, version.Kind);
        Assert.Equal(expected, version.Raw);
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("Version 1.2.3", "1.2.3")]
    [InlineData("version-1.2.3", "1.2.3")]
    [InlineData("1.2.3+build.99", "1.2.3")]
    [InlineData("  1.2.3  ", "1.2.3")]
    [InlineData("01.02.03", "1.2.3")]
    public void Vendor_prefixes_padding_and_build_metadata_do_not_reach_the_normalized_form(
        string raw,
        string normalized)
    {
        Assert.Equal(normalized, VersionValue.Parse(raw).Normalized);
    }

    [Fact]
    public void A_leading_v_that_is_not_a_version_prefix_is_kept()
    {
        // "vulkan" is not "v" + digits, so nothing is stripped.
        Assert.Equal("vulkan", VersionValue.Parse("vulkan").Normalized);
    }

    [Fact]
    public void Build_metadata_alone_leaves_nothing_to_compare()
    {
        var version = VersionValue.Parse("+build");

        Assert.Equal(VersionKind.Opaque, version.Kind);
        Assert.Equal(string.Empty, version.Normalized);
        Assert.Empty(version.Core);
    }

    [Fact]
    public void The_normalized_form_joins_core_components_with_dots()
    {
        var version = VersionValue.Parse("3.2.0.9001");

        Assert.Equal("3.2.0.9001", version.Normalized);
        Assert.False(version.HasPreRelease);
        Assert.Empty(version.PreRelease);
    }

    [Fact]
    public void The_normalized_form_appends_the_pre_release_after_a_single_dash()
    {
        var version = VersionValue.Parse("1.2.3-rc.1");

        Assert.True(version.HasPreRelease);
        Assert.Equal("1.2.3-rc.1", version.Normalized);
        Assert.Equal(["rc", "1"], version.PreRelease.Select(t => t.Text));
    }

    [Fact]
    public void A_release_normalizes_without_a_trailing_dash()
    {
        Assert.DoesNotContain('-', VersionValue.Parse("1.2.3").Normalized);
    }

    [Theory]
    [InlineData("1.2.3-rc1", "1.2.3-rc.1")]
    [InlineData("2.0.0-beta2", "2.0.0-beta.2")]
    public void The_pre_release_tag_starts_after_the_dash(string raw, string normalized)
    {
        var version = VersionValue.Parse(raw);

        Assert.True(version.HasPreRelease);
        Assert.Equal(normalized, version.Normalized);
    }

    [Fact]
    public void A_trailing_dash_does_not_open_an_empty_pre_release()
    {
        var version = VersionValue.Parse("1.2-");

        Assert.False(version.HasPreRelease);
        Assert.Equal("1.2", version.Normalized);
    }

    [Fact]
    public void A_dash_after_a_non_numeric_head_is_not_a_pre_release_separator()
    {
        var version = VersionValue.Parse("1.2b-rc1");

        Assert.False(version.HasPreRelease);
        Assert.Equal("1.2.b.rc.1", version.Normalized);
    }

    [Theory]
    [InlineData("2024-11-01", "2024.11.1")]
    [InlineData("2024-11", "2024.11")]
    public void A_dash_separated_date_stamp_is_a_separator_not_a_pre_release(string raw, string normalized)
    {
        var version = VersionValue.Parse(raw);

        Assert.False(version.HasPreRelease);
        Assert.Equal(VersionKind.Date, version.Kind);
        Assert.Equal(normalized, version.Normalized);
    }

    [Theory]
    [InlineData("1.2.3", VersionKind.SemVer)]
    [InlineData("1.2.3-rc1", VersionKind.SemVer)]
    [InlineData("1.2-rc1", VersionKind.SemVer)]
    [InlineData("3.2.0.9001", VersionKind.DottedNumeric)]
    [InlineData("12345", VersionKind.DottedNumeric)]
    [InlineData("10000101", VersionKind.DottedNumeric)]
    [InlineData("1.2 HF3", VersionKind.Mixed)]
    [InlineData("4.1b7", VersionKind.Mixed)]
    [InlineData("2024.11.1a", VersionKind.Mixed)]
    [InlineData("unreleased", VersionKind.Opaque)]
    public void Version_shapes_are_classified_mechanically(string raw, VersionKind expected)
    {
        Assert.Equal(expected, VersionValue.Parse(raw).Kind);
    }

    [Theory]
    [InlineData("19900101")]
    [InlineData("29991231")]
    [InlineData("20241101")]
    [InlineData("1990.1.1")]
    [InlineData("2999.12.31")]
    [InlineData("2024.11")]
    [InlineData("2024.1.5")]
    [InlineData("2024.12.5")]
    [InlineData("2024.11.31")]
    public void Date_stamps_are_recognised_at_their_boundaries(string raw)
    {
        Assert.Equal(VersionKind.Date, VersionValue.Parse(raw).Kind);
    }

    [Theory]
    [InlineData("19891231")]
    [InlineData("30000101")]
    [InlineData("19901301")]
    [InlineData("19900001")]
    [InlineData("19900132")]
    [InlineData("19900100")]
    [InlineData("1989.12.31")]
    [InlineData("3000.1.1")]
    [InlineData("2024.13.1")]
    [InlineData("2024.0.1")]
    [InlineData("2024.11.45")]
    [InlineData("2024.11.0")]
    public void Numbers_outside_a_plausible_date_are_not_date_stamps(string raw)
    {
        Assert.NotEqual(VersionKind.Date, VersionValue.Parse(raw).Kind);
    }

    [Fact]
    public void A_numeric_run_too_large_for_a_long_saturates_rather_than_throwing()
    {
        var version = VersionValue.Parse("99999999999999999999999.1");

        Assert.Equal(long.MaxValue, version.Core[0].Number);
        Assert.Equal(1, version.Core[1].Number);
    }

    [Fact]
    public void A_run_of_zeroes_parses_as_zero()
    {
        var version = VersionValue.Parse("1.000.2");

        Assert.Equal(0, version.Core[1].Number);
        Assert.Equal("1.0.2", version.Normalized);
    }

    [Fact]
    public void Alphabetic_runs_are_lowercased_and_separators_are_dropped()
    {
        var version = VersionValue.Parse("1.2 HF3");

        Assert.Equal(["1", "2", "hf", "3"], version.Core.Select(t => t.Text));
        Assert.False(version.Core[2].IsNumeric);
    }

    [Fact]
    public void Equality_and_hashing_are_by_raw_string()
    {
        var a = VersionValue.Parse("1.2.3");
        var b = VersionValue.Parse("1.2.3");
        var c = VersionValue.Parse("v1.2.3");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.False(a.Equals(null));
        Assert.False(a.Equals((object?)"1.2.3"));
    }

    [Fact]
    public void A_numeric_token_renders_as_its_number()
    {
        Assert.Equal("7", VersionToken.Numeric(7).ToString());
        Assert.Equal("rc", VersionToken.Alpha("rc").ToString());
    }
}
