using SwgCraft.Core;

namespace SwgCraft.Data;

/// <summary>Works out the default price per unit (CPU) for a resource when it is first added to inventory.</summary>
public interface IUnitPriceCalculator
{
    /// <summary>Returns null when no price can be worked out; the price is then left blank for you to enter.</summary>
    double? Calculate(Spawn spawn, ResourceNode? resourceType);
}

/// <summary>Leaves the price blank. Used before the real calculator is registered.</summary>
public sealed class BlankPriceCalculator : IUnitPriceCalculator
{
    public double? Calculate(Spawn spawn, ResourceNode? resourceType) => null;
}

/// <summary>The JnF calculation, using the stat maximums from the resource type's ranges in the tree.</summary>
public sealed class JnfUnitPriceCalculator : IUnitPriceCalculator
{
    readonly Func<PriceSettings> _settings;

    public JnfUnitPriceCalculator(Func<PriceSettings> settings) => _settings = settings;

    public double? Calculate(Spawn spawn, ResourceNode? resourceType)
    {
        var p = _settings();
        if (p.Check() != null || resourceType?.Ranges == null) return null;
        var maxima = resourceType.Ranges.ToDictionary(kv => kv.Key, kv => kv.Value.Max);
        return CpuPrice.Compute(spawn.Stats, maxima, p);
    }
}

public static class UnitPricing
{
    public static IUnitPriceCalculator Calculator { get; set; } = new BlankPriceCalculator();
}
