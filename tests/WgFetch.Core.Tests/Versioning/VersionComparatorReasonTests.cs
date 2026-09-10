using WgFetch.Core.Versioning;

namespace WgFetch.Core.Tests.Versioning;

/// <summary>
/// The comparator must be deterministic and total, and must say which branch decided a comparison so
/// that an escalation to the LLM tier is recorded rather than silent (docs/REQUIREMENTS.md, "Version
/// handling"). These tests pin the decision branch and its reason, not just the sign.
/// </summary>
public sealed class VersionComparatorReasonTests
{
    private static readonly VersionComparator Comparator = VersionComparator.Instance;

    [Fact]
    public void Absent_versions_are_ordered_before_present_ones_with_a_stated_reason()
    {
        var present = VersionValue.Parse("1.2.3");

        var bothAbsent = Comparator.CompareDetailed((VersionValue?)null, null);
        var leftAbsent = Comparator.CompareDetailed(null, present);
        var rightAbsent = Comparator.CompareDetailed(present, null);

        Assert.Equal(new VersionComparison(0, true, "both versions absent"), bothAbsent);
        Assert.Equal(new VersionComparison(-1, true, "left version absent"), leftAbsent);
        Assert.Equal(new VersionComparison(1, true, "right version absent"), rightAbsent);
    }

    [Fact]
    public void Identical_raw_strings_short_circuit()
    {
        var result = Comparator.CompareDetailed("1.2.3", "1.2.3");

        Assert.Equal(new VersionComparison(0, true, "identical version strings"), result);
    }

    [Fact]
    public void Different_spellings_of_the_same_version_are_equal_after_normalization()
    {
        var result = Comparator.CompareDetailed("1.2", "1.2.0");

        Assert.Equal(0, result.Order);
        Assert.True(result.Decidable);
        Assert.Equal("equal after normalization", result.Reason);
        Assert.Equal(0, Comparator.Compare("v3.2.0", "3.2.0+meta"));
    }

    [Fact]
    public void Numeric_components_are_compared_numerically_and_the_component_is_named()
    {
        var newer = Comparator.CompareDetailed("1.10.0", "1.9.0");

        Assert.Equal(1, newer.Order);
        Assert.True(newer.Decidable);
        Assert.Equal("component 2: 10 vs 9", newer.Reason);
        Assert.Equal(-1, Comparator.CompareDetailed("1.9.0", "1.10.0").Order);
    }

    [Fact]
    public void An_absent_numeric_component_is_treated_as_zero_but_a_present_one_decides()
    {
        var shorter = Comparator.CompareDetailed("1.2", "1.2.1");
        var longer = Comparator.CompareDetailed("1.2.1", "1.2");

        Assert.Equal(-1, shorter.Order);
        Assert.True(shorter.Decidable);
        Assert.Equal("component 3 absent on the left, '1' on the right", shorter.Reason);

        Assert.Equal(1, longer.Order);
        Assert.True(longer.Decidable);
        Assert.Equal("component 3 '1' on the left, absent on the right", longer.Reason);
    }

    [Fact]
    public void Alphabetic_components_at_the_same_position_compare_ordinally()
    {
        var result = Comparator.CompareDetailed("1.2b", "1.2a");

        Assert.Equal(1, result.Order);
        Assert.Equal("component 3: 'b' vs 'a'", result.Reason);
    }

    [Fact]
    public void A_numeric_component_outranks_an_alphabetic_one_at_the_same_position()
    {
        var leftNumeric = Comparator.CompareDetailed("1.2.1", "1.2.rc");
        var rightNumeric = Comparator.CompareDetailed("1.2.rc", "1.2.1");

        Assert.Equal(1, leftNumeric.Order);
        Assert.Equal("component 3: numeric 1 outranks 'rc'", leftNumeric.Reason);

        Assert.Equal(-1, rightNumeric.Order);
        Assert.Equal("component 3: numeric 1 outranks 'rc'", rightNumeric.Reason);
    }

    [Fact]
    public void A_trailing_alphabetic_suffix_orders_deterministically_but_ambiguously()
    {
        var hotfix = Comparator.CompareDetailed("1.2 HF3", "1.2");

        Assert.Equal(-1, hotfix.Order);
        Assert.False(hotfix.Decidable);
        Assert.StartsWith("ambiguous suffix; ", hotfix.Reason, StringComparison.Ordinal);
        Assert.Contains("component 3 'hf' on the left, absent on the right", hotfix.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_alphabetic_suffix_on_the_right_is_equally_ambiguous()
    {
        var beta = Comparator.CompareDetailed("4.1", "4.1b");

        Assert.Equal(1, beta.Order);
        Assert.False(beta.Decidable);
        Assert.StartsWith("ambiguous suffix; ", beta.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_numeric_component_is_not_ambiguous()
    {
        var result = Comparator.CompareDetailed("1.2.1", "1.2");

        Assert.True(result.Decidable);
        Assert.DoesNotContain("ambiguous", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pre_release_ranks_below_the_release_of_the_same_core()
    {
        var result = Comparator.CompareDetailed("1.2.3-rc1", "1.2.3");

        Assert.Equal(-1, result.Order);
        Assert.True(result.Decidable);
        Assert.Equal("pre-release ranks below release", result.Reason);
        Assert.Equal(1, Comparator.CompareDetailed("1.2.3", "1.2.3-rc1").Order);
    }

    [Fact]
    public void Pre_release_tags_are_compared_against_each_other()
    {
        var result = Comparator.CompareDetailed("1.2.3-rc1", "1.2.3-rc2");

        Assert.Equal(-1, result.Order);
        Assert.True(result.Decidable);
        Assert.Equal("pre-release: component 2: 1 vs 2", result.Reason);
    }

    [Fact]
    public void An_ambiguous_pre_release_suffix_is_reported_as_undecidable()
    {
        var result = Comparator.CompareDetailed("1.2.3-rc.1", "1.2.3-rc.1a");

        Assert.Equal(1, result.Order);
        Assert.False(result.Decidable);
        Assert.Contains("pre-release:", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Incomparable_shapes_stay_ordered_but_are_flagged_for_escalation()
    {
        var result = Comparator.CompareDetailed("2024.11.1", "1.2.3");

        Assert.False(result.Decidable);
        Assert.StartsWith("incomparable version shapes (Date vs SemVer); ", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_opaque_version_is_never_comparable_even_against_a_well_formed_one()
    {
        var result = Comparator.CompareDetailed("latest", "1.2.3");

        Assert.False(result.Decidable);
        Assert.Contains("incomparable version shapes (Opaque vs SemVer)", result.Reason, StringComparison.Ordinal);
        Assert.False(Comparator.CompareDetailed("1.2.3", "latest").Decidable);
    }

    [Fact]
    public void Two_date_stamps_are_comparable_with_each_other()
    {
        var result = Comparator.CompareDetailed("2024.11.1", "2024.10.9");

        Assert.Equal(1, result.Order);
        Assert.True(result.Decidable);
        Assert.DoesNotContain("incomparable", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_order_is_always_normalised_to_minus_one_zero_or_one()
    {
        Assert.Equal(1, Comparator.CompareDetailed("2.0.0", "1.0.0").Order);
        Assert.Equal(-1, Comparator.CompareDetailed("1.0.0", "9.0.0").Order);
        Assert.Equal(0, Comparator.CompareDetailed("1.0", "1.0.0").Order);
    }

    [Fact]
    public void Newest_returns_the_highest_version_and_keeps_the_first_of_equals()
    {
        VersionValue[] versions =
        [
            VersionValue.Parse("1.2"),
            VersionValue.Parse("1.2.0"),
            VersionValue.Parse("1.1.9"),
        ];

        var newest = Comparator.Newest(versions);

        Assert.NotNull(newest);
        Assert.Equal("1.2", newest!.Raw);
        Assert.Null(Comparator.Newest([]));
        Assert.Equal("3.0.0", Comparator.Newest([VersionValue.Parse("1.0.0"), VersionValue.Parse("3.0.0")])!.Raw);
    }

    [Fact]
    public void The_string_and_value_overloads_agree()
    {
        Assert.Equal(
            Comparator.Compare("1.2.3", "1.2.4"),
            Comparator.Compare(VersionValue.Parse("1.2.3"), VersionValue.Parse("1.2.4")));
        Assert.Equal(-1, Comparator.Compare((string?)null, "1.2.3"));
        Assert.Equal(0, Comparator.Compare((VersionValue?)null, null));
    }
}
