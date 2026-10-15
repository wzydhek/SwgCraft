namespace SwgCraft.Core;

/// <summary>Grades a stat against the maximum its resource type can have (used for the green highlighting).</summary>
public static class StatQuality
{
    public const double DefaultHigh = 0.90;   // light green
    public const double DefaultTop = 0.95;    // darker green

    /// <summary>0 = no highlight, 1 = at least <paramref name="high"/> of max, 2 = at least <paramref name="top"/> of max.</summary>
    public static int Tier(int value, int max, double high = DefaultHigh, double top = DefaultTop)
    {
        if (max <= 0) return 0;
        double ratio = (double)value / max;
        if (ratio >= top) return 2;
        if (ratio >= high) return 1;
        return 0;
    }
}
