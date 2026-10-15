namespace SwgCraft.Core;

/// <summary>
/// Constants behind the default price per unit (JnF: Control Panel C17, C21:C23 and Resources O2:O3).
/// Edited by hand in settings.json under "pricing". The two thresholds also drive the green stat highlighting.
/// </summary>
public sealed class PriceSettings
{
    public double MaxPrice { get; set; } = 10;            // Control Panel C17: Max CPU Sales Price
    public double WeightStatTotals { get; set; } = 0.4;   // C21: stat sum vs. total of the stat maximums
    public double WeightHigh { get; set; } = 0.3;         // C22: share of stats at or above HighThreshold (and below TopThreshold)
    public double WeightTop { get; set; } = 0.3;          // C23: share of stats at or above TopThreshold
    public double TopThreshold { get; set; } = 0.95;      // Resources O2: Percent Threshold Stage 1
    public double HighThreshold { get; set; } = 0.90;     // Resources O3: Percent Threshold Stage 2
    public double MinPrice { get; set; } = 1;             // the MAX(1, ...) floor in the sheet
    public int Decimals { get; set; } = 1;                // decimal places kept in the price
    public string Rounding { get; set; } = "nearest";          // "up" = always round up, "nearest", or "none" (the sheet's raw value)

    /// <summary>Returns a description of the first problem, or null when the settings are usable.</summary>
    public string? Check()
    {
        if (!(MaxPrice > 0)) return "maxPrice must be greater than 0";
        if (!(MinPrice >= 0)) return "minPrice cannot be negative";
        if (WeightStatTotals < 0 || WeightHigh < 0 || WeightTop < 0) return "weights cannot be negative";
        if (Math.Abs(WeightStatTotals + WeightHigh + WeightTop - 1) > 1e-9)
            return $"the three weights must add up to 1 (they add up to {WeightStatTotals + WeightHigh + WeightTop:0.####})";
        if (!(HighThreshold > 0) || HighThreshold > TopThreshold || TopThreshold > 1)
            return "thresholds must satisfy 0 < highThreshold <= topThreshold <= 1";
        if (Decimals < 0 || Decimals > 4) return "decimals must be between 0 and 4";
        if ((Rounding ?? "").ToLowerInvariant() is not ("up" or "nearest" or "none"))
            return "rounding must be \"up\", \"nearest\" or \"none\"";
        return null;
    }
}

/// <summary>The JnF "CPU" calculation (Resources!CA), written as one pure function.</summary>
public static class CpuPrice
{
    /// <summary>
    /// <paramref name="stats"/> are the resource's stats (a missing stat counts as a blank cell);
    /// <paramref name="maxima"/> are the maximums for its resource type (a missing or 0 maximum counts as "no maximum").
    /// Returns null where the sheet would show #DIV/0!: a resource with no stats, or a type with no maximums.
    /// The result is rounded as configured (default: up, to one decimal place).
    /// </summary>
    public static double? Compute(IReadOnlyDictionary<Stat, int> stats, IReadOnlyDictionary<Stat, int> maxima, PriceSettings p)
    {
        int present = 0;       // BT: stats with a value above 0
        long current = 0;      // BZ: sum of the resource's stats
        long possible = 0;     // BY: sum of the type's stat maximums
        int mid = 0, top = 0;  // BU and BV

        foreach (var stat in StatInfo.All)
        {
            stats.TryGetValue(stat, out var value);
            maxima.TryGetValue(stat, out var max);
            current += value;
            if (value > 0) present++;
            if (max <= 0) continue;                  // BF:BP is 0 when there is no maximum

            possible += max;
            double ratio = (double)value / max;
            if (ratio >= p.TopThreshold) top++;      // inclusive on purpose: the sheet itself uses ">", so a stat at
            else if (ratio >= p.HighThreshold) mid++; // exactly 90% or 95% of its maximum is counted here but not there
        }

        if (present == 0 || possible == 0) return null;

        double s = (double)current / possible * p.MaxPrice;
        double price = s * (p.WeightStatTotals + p.WeightHigh * mid / present + p.WeightTop * top / present);
        return RoundPrice(Math.Max(p.MinPrice, price), p);
    }

    /// <summary>
    /// Applies the configured rounding. The value is first rounded to 9 decimals of the scaled number so floating-point
    /// noise cannot push an exact price up: 1.2000000000000002 must stay 1.2, not become 1.3.
    /// </summary>
    public static double RoundPrice(double value, PriceSettings p)
    {
        double scale = Math.Pow(10, Math.Clamp(p.Decimals, 0, 6));
        double scaled = Math.Round(value * scale, 9);
        return (p.Rounding ?? "").ToLowerInvariant() switch
        {
            "up" => Math.Ceiling(scaled) / scale,
            "nearest" => Math.Round(scaled, MidpointRounding.AwayFromZero) / scale,
            _ => value,
        };
    }
}
