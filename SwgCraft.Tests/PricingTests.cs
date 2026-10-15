using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public class PricingTests
{
    static readonly PriceSettings Defaults = new();   // 10, weights 0.4/0.3/0.3, thresholds 95%/90%, floor 1
    const int Precision = 9;

    static IReadOnlyDictionary<Stat, int> S(params (Stat Stat, int Value)[] items) =>
        items.ToDictionary(i => i.Stat, i => i.Value);

    static double? Price(IReadOnlyDictionary<Stat, int> stats, IReadOnlyDictionary<Stat, int> maxima) =>
        CpuPrice.Compute(stats, maxima, Defaults);

    [Fact]
    public void Default_settings_are_valid() => Assert.Null(Defaults.Check());

    // The expected numbers below come from a cell-by-cell transcription of the JnF formulas.

    [Fact]
    public void Two_stat_resource()
    {
        // OQ 960/1000 is above 95%, UT 920/1000 is between 90% and 95%: 9.4 * (0.4 + 0.3*0.5 + 0.3*0.5)
        var price = Price(S((Stat.OQ, 960), (Stat.UT, 920)), S((Stat.OQ, 1000), (Stat.UT, 1000)));
        Assert.Equal(6.6, price!.Value, Precision);
    }

    [Fact]
    public void Three_stats_with_different_maximums()
    {
        var stats = S((Stat.ER, 480), (Stat.CR, 920), (Stat.OQ, 400));
        var maxima = S((Stat.ER, 500), (Stat.CR, 1000), (Stat.OQ, 800));
        Assert.Equal(4.7, Price(stats, maxima)!.Value, Precision);
    }

    [Fact]
    public void A_perfect_resource_only_reaches_seventy_percent_of_the_max_price()
    {
        // As in the sheet: a stat counts in the 95% bucket OR the 90% bucket, never both,
        // so the 0.3 weight of the 90% bucket is unused when every stat is above 95%.
        var price = Price(S((Stat.OQ, 1000), (Stat.UT, 1000)), S((Stat.OQ, 1000), (Stat.UT, 1000)));
        Assert.Equal(7.0, price!.Value, Precision);
    }

    [Theory]
    [InlineData(100, 1.0)]      // 0.4 would be below the floor
    [InlineData(899, 3.6)]    // just under 90%
    [InlineData(900, 6.3)]      // exactly 90% counts (the sheet itself would not count it)
    [InlineData(901, 6.3)]
    [InlineData(949, 6.6)]
    [InlineData(950, 6.7)]     // exactly 95%: top bucket; equal weights make the price the same as the 90% bucket
    [InlineData(951, 6.7)]
    [InlineData(1000, 7.0)]
    public void Single_stat_thresholds_are_inclusive(int oq, double expected) =>
        Assert.Equal(expected, Price(S((Stat.OQ, oq)), S((Stat.OQ, 1000)))!.Value, Precision);

    [Fact]
    public void Exactly_95_percent_counts_as_the_top_bucket()
    {
        // With unequal weights the two buckets differ: top = 0.4 + 0.4, 90% bucket = 0.4 + 0.2.
        var settings = new PriceSettings { WeightStatTotals = 0.4, WeightHigh = 0.2, WeightTop = 0.4 };
        var price = CpuPrice.Compute(S((Stat.OQ, 950)), S((Stat.OQ, 1000)), settings);
        Assert.Equal(7.6, price!.Value, Precision);
    }

    static IReadOnlyDictionary<Stat, int> ByStat(int[] values) =>
        StatInfo.All.Zip(values).Where(p => p.Second > 0).ToDictionary(p => p.First, p => p.Second);

    [Theory]
    [InlineData("aokiaism, Vendusii Crystal Amorphous Gemstone",
        new[] { 643, 770, 0, 786, 0, 936, 491, 0, 127, 914, 929 },
        new[] { 744, 930, 0, 930, 0, 972, 558, 0, 1000, 930, 930 }, 4.1)]
    [InlineData("oideaium, Platinite Copper",
        new[] { 0, 629, 912, 594, 0, 548, 906, 0, 678, 734, 731 },
        new[] { 0, 738, 961, 646, 0, 615, 961, 0, 1000, 761, 761 }, 5.2)]
    [InlineData("febeboo, Crystallized Bicorbantium Steel",
        new[] { 0, 493, 255, 666, 0, 543, 702, 0, 44, 238, 505 },
        new[] { 0, 1000, 1000, 1000, 0, 1000, 1000, 0, 1000, 1000, 1000 }, 1.7)]
    public void Matches_the_JnF_sheet(string resource, int[] stats, int[] maxima, double sheetValue)
    {
        // The sheet shows one decimal, so compare at one decimal.
        var price = Price(ByStat(stats), ByStat(maxima))!.Value;
        Assert.Equal(sheetValue, Math.Round(price, 1, MidpointRounding.AwayFromZero), 6);
    }

    [Fact]
    public void Missing_data_gives_no_price()
    {
        Assert.Null(Price(S(), S((Stat.OQ, 1000))));                        // no stats
        Assert.Null(Price(S((Stat.OQ, 0)), S((Stat.OQ, 1000))));            // stats are all 0
        Assert.Null(Price(S((Stat.OQ, 500)), S()));                         // no maximums
        Assert.Null(Price(S((Stat.OQ, 500)), S((Stat.OQ, 0))));
    }

    [Fact]
    public void Weights_must_add_up_to_one()
    {
        Assert.NotNull(new PriceSettings { WeightHigh = 0.4 }.Check());
        Assert.Null(new PriceSettings { WeightStatTotals = 0.5, WeightHigh = 0.2, WeightTop = 0.3 }.Check());
    }

    [Fact]
    public void Bad_thresholds_and_prices_are_rejected()
    {
        Assert.NotNull(new PriceSettings { HighThreshold = 0.96 }.Check());   // above the top threshold
        Assert.NotNull(new PriceSettings { TopThreshold = 1.2 }.Check());
        Assert.NotNull(new PriceSettings { MaxPrice = 0 }.Check());
        Assert.NotNull(new PriceSettings { MinPrice = -1 }.Check());
    }

    // ------------------------------------------------------------ calculator over the resource tree

    static Spawn MakeSpawn(IReadOnlyDictionary<Stat, int> stats)
    {
        var now = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        return new Spawn(1, 118, "alpha", "t", stats, now, now, now, null, "");
    }

    static ResourceNode Leaf(params (Stat Stat, int Max)[] maxima) => new()
    {
        Id = "t", Name = "T",
        Ranges = maxima.ToDictionary(m => m.Stat, m => new StatRange { Min = 1, Max = m.Max }).ToSorted(),
    };

    [Fact]
    public void Calculator_uses_the_type_maximums_from_the_tree()
    {
        var calc = new JnfUnitPriceCalculator(() => new PriceSettings());
        var spawn = MakeSpawn(S((Stat.OQ, 960), (Stat.UT, 920)));
        Assert.Equal(6.6, calc.Calculate(spawn, Leaf((Stat.OQ, 1000), (Stat.UT, 1000)))!.Value, Precision);
    }

    [Fact]
    public void Calculator_returns_nothing_for_unknown_types_or_invalid_settings()
    {
        var spawn = MakeSpawn(S((Stat.OQ, 960)));
        var good = new JnfUnitPriceCalculator(() => new PriceSettings());
        Assert.Null(good.Calculate(spawn, null));
        Assert.Null(good.Calculate(spawn, new ResourceNode { Id = "t", Name = "T" }));   // no ranges

        var broken = new JnfUnitPriceCalculator(() => new PriceSettings { WeightTop = 0.9 });
        Assert.Null(broken.Calculate(spawn, Leaf((Stat.OQ, 1000))));
    }

    // ------------------------------------------------------------ settings file and highlighting

    [Fact]
    public void Old_settings_files_without_pricing_still_load_with_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "schemaVersion": 1, "activeGalaxyId": 118 }""");
            var s = JsonFile.Read<AppSettings>(path);
            Assert.Equal(10, s.Pricing.MaxPrice);
            Assert.Null(s.Pricing.Check());

            s.Pricing.MaxPrice = 12;
            JsonFile.WriteAtomic(path, s);
            Assert.Equal(12, JsonFile.Read<AppSettings>(path).Pricing.MaxPrice);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void Highlight_thresholds_can_be_changed()
    {
        Assert.Equal(0, StatQuality.Tier(850, 1000));
        Assert.Equal(1, StatQuality.Tier(850, 1000, 0.85, 0.95));
        Assert.Equal(2, StatQuality.Tier(980, 1000, 0.85, 0.98));
    }
}

static class PricingTestExtensions
{
    public static SortedDictionary<Stat, StatRange> ToSorted(this Dictionary<Stat, StatRange> d) => new(d);
}
