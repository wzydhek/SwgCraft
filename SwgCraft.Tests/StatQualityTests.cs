using SwgCraft.Core;

namespace SwgCraft.Tests;

public class StatQualityTests
{
    [Theory]
    [InlineData(899, 1000, 0)]
    [InlineData(900, 1000, 1)]
    [InlineData(949, 1000, 1)]
    [InlineData(950, 1000, 2)]
    [InlineData(1000, 1000, 2)]
    [InlineData(719, 800, 0)]
    [InlineData(720, 800, 1)]   // 90% of 800
    [InlineData(759, 800, 1)]
    [InlineData(760, 800, 2)]   // 95% of 800
    [InlineData(500, 0, 0)]     // no known maximum: never highlighted
    [InlineData(0, 1000, 0)]
    public void Tier_follows_the_90_and_95_percent_thresholds(int value, int max, int expected) =>
        Assert.Equal(expected, StatQuality.Tier(value, max));
}
