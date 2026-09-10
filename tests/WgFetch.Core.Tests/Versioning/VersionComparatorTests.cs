using WgFetch.Core.Versioning;

namespace WgFetch.Core.Tests.Versioning;

/// <summary>
/// The comparator must be deterministic and total across semver, dotted-numeric, date-based and mixed
/// alphanumeric forms, and must say when it cannot order two versions
/// (docs/REQUIREMENTS.md, "Version handling").
/// </summary>
public sealed class VersionComparatorTests
{
    private static readonly VersionComparator Comparator = VersionComparator.Instance;

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("3.2.0.9001", "3.2.0.9002")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2")]
    [InlineData("2024.11.1", "2024.12.1")]
    [InlineData("2024.11.1", "2025.1.1")]
    [InlineData("4.1b7", "4.1b8")]
    [InlineData("4.1b7", "4.2")]
    [InlineData("1.2 HF3", "1.2 HF4")]
    [InlineData("v1.2.3", "v1.2.4")]
    public void Orders_known_pairs_ascending(string lower, string higher)
    {
        Assert.True(Comparator.CompareDetailed(lower, higher).Order < 0, $"expected {lower} < {higher}");
        Assert.True(Comparator.CompareDetailed(higher, lower).Order > 0, $"expected {higher} > {lower}");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.0.0", "1.2")]
    [InlineData("v3.2.0", "3.2.0")]
    [InlineData("version 3.2.0", "3.2.0")]
    [InlineData("1.2.3+build.5", "1.2.3+build.9")]
    public void Treats_equivalent_forms_as_equal(string left, string right) =>
        Assert.Equal(0, Comparator.CompareDetailed(left, right).Order);

    [Fact]
    public void Reports_undecidable_when_a_date_version_meets_a_dotted_version()
    {
        var comparison = Comparator.CompareDetailed("2024.11.1", "3.2.0.9001");

        Assert.False(comparison.Decidable);
        Assert.Contains("incomparable", comparison.Reason, StringComparison.Ordinal);

        // Still deterministic and antisymmetric, so sorting remains stable.
        Assert.Equal(-Comparator.CompareDetailed("3.2.0.9001", "2024.11.1").Order, comparison.Order);
    }

    [Theory]
    [InlineData("1.2", "1.2 HF3")]
    [InlineData("4.1", "4.1b7")]
    public void Reports_undecidable_when_only_a_trailing_alphabetic_suffix_differs(string bare, string suffixed)
    {
        // "HF3" is newer than its base version while "b7" is older; nothing mechanical can tell them
        // apart, so the comparator orders them deterministically but tells the caller it guessed.
        var comparison = Comparator.CompareDetailed(bare, suffixed);

        Assert.False(comparison.Decidable);
        Assert.NotEqual(0, comparison.Order);
        Assert.Equal(-Comparator.CompareDetailed(suffixed, bare).Order, comparison.Order);
    }

    [Fact]
    public void Reports_decidable_for_ordinary_comparisons()
    {
        Assert.True(Comparator.CompareDetailed("1.2.3", "1.2.4").Decidable);
        Assert.Contains("component", Comparator.CompareDetailed("1.2.3", "1.2.4").Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2024.11.1", VersionKind.Date)]
    [InlineData("2024-11-01", VersionKind.Date)]
    [InlineData("20241101", VersionKind.Date)]
    [InlineData("1.2.3", VersionKind.SemVer)]
    [InlineData("1.0.0-rc.1", VersionKind.SemVer)]
    [InlineData("3.2.0.9001", VersionKind.DottedNumeric)]
    [InlineData("1.2 HF3", VersionKind.Mixed)]
    [InlineData("4.1b7", VersionKind.Mixed)]
    [InlineData("", VersionKind.Opaque)]
    [InlineData("unreleased", VersionKind.Opaque)]
    public void Classifies_version_shapes(string raw, VersionKind expected) =>
        Assert.Equal(expected, VersionValue.Parse(raw).Kind);

    [Fact]
    public void Newest_picks_the_highest_of_a_mixed_set()
    {
        var versions = new[] { "3.1.2.9001", "3.2.0.9001", "3.0.0" }.Select(VersionValue.Parse).ToArray();

        Assert.Equal("3.2.0.9001", Comparator.Newest(versions)?.Raw);
    }

    [Fact]
    public void Parsing_never_throws_on_hostile_input()
    {
        var hostile = new[]
        {
            null, string.Empty, "  ", "...", "-", "v", "999999999999999999999999999999",
            "1.2.3.4.5.6.7.8.9.10", "\u0000\u0001", new string('9', 4096), "1..2", "1.-2.3",
        };

        foreach (var raw in hostile)
        {
            var parsed = VersionValue.Parse(raw);
            Assert.NotNull(parsed.Normalized);
            _ = Comparator.CompareDetailed(parsed, VersionValue.Parse("1.0.0"));
        }
    }
}

/// <summary>Property-based coverage: comparison must be total, antisymmetric and transitive.</summary>
public sealed class VersionComparatorPropertyTests
{
    private static readonly VersionComparator Comparator = VersionComparator.Instance;

    private static IReadOnlyList<string> Corpus(int seed, int count)
    {
        var random = new Random(seed);
        var values = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(Generate(random));
        }

        return values;
    }

    private static string Generate(Random random) => random.Next(6) switch
    {
        0 => $"{random.Next(0, 6)}.{random.Next(0, 12)}.{random.Next(0, 20)}",
        1 => $"{random.Next(0, 6)}.{random.Next(0, 12)}.{random.Next(0, 20)}.{random.Next(0, 9999)}",
        2 => $"{random.Next(2019, 2027)}.{random.Next(1, 13)}.{random.Next(1, 29)}",
        3 => $"{random.Next(0, 6)}.{random.Next(0, 12)}{(char)('a' + random.Next(0, 4))}{random.Next(0, 9)}",
        4 => $"{random.Next(0, 6)}.{random.Next(0, 12)} HF{random.Next(0, 6)}",
        _ => $"{random.Next(0, 6)}.{random.Next(0, 12)}.{random.Next(0, 20)}-{(random.Next(2) == 0 ? "alpha" : "rc")}.{random.Next(0, 5)}",
    };

    [Theory]
    [InlineData(11)]
    [InlineData(2718)]
    [InlineData(31415)]
    public void Comparison_is_antisymmetric_and_reflexive(int seed)
    {
        var corpus = Corpus(seed, 120).Select(VersionValue.Parse).ToArray();

        foreach (var a in corpus)
        {
            Assert.Equal(0, Comparator.CompareDetailed(a, a).Order);

            foreach (var b in corpus)
            {
                var forward = Comparator.CompareDetailed(a, b).Order;
                var backward = Comparator.CompareDetailed(b, a).Order;
                Assert.Equal(-Math.Sign(forward), Math.Sign(backward));
            }
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(1234)]
    public void Comparison_is_transitive(int seed)
    {
        var corpus = Corpus(seed, 45).Select(VersionValue.Parse).ToArray();

        foreach (var a in corpus)
        {
            foreach (var b in corpus)
            {
                foreach (var c in corpus)
                {
                    var ab = Math.Sign(Comparator.CompareDetailed(a, b).Order);
                    var bc = Math.Sign(Comparator.CompareDetailed(b, c).Order);
                    var ac = Math.Sign(Comparator.CompareDetailed(a, c).Order);

                    if (ab < 0 && bc < 0)
                    {
                        Assert.True(ac < 0, $"transitivity violated: {a} < {b} < {c} but {a} !< {c}");
                    }
                    else if (ab > 0 && bc > 0)
                    {
                        Assert.True(ac > 0, $"transitivity violated: {a} > {b} > {c} but {a} !> {c}");
                    }
                    else if (ab == 0 && bc == 0)
                    {
                        Assert.Equal(0, ac);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(99)]
    public void Sorting_is_stable_regardless_of_input_order(int seed)
    {
        var corpus = Corpus(seed, 60).Select(VersionValue.Parse).ToArray();
        var shuffled = corpus.OrderBy(v => v.Raw.GetHashCode(StringComparison.Ordinal)).ToArray();

        var a = corpus.OrderBy(v => v, Comparator).Select(v => v.Normalized).ToArray();
        var b = shuffled.OrderBy(v => v, Comparator).Select(v => v.Normalized).ToArray();

        Assert.Equal(a, b);
    }
}
