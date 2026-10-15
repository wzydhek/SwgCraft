using System.Windows.Media;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

/// <summary>
/// The green stat highlighting used by the resource inventory: a stat at 90% / 95% (the thresholds in the pricing settings)
/// of the most its resource type can have gets a light / darker green background.
/// </summary>
public static class StatHighlight
{
    static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static readonly Brush HighBrush = Make(0xC8, 0xE6, 0xC9);   // light green: 90% of max or more
    public static readonly Brush TopBrush = Make(0x81, 0xC7, 0x84);    // darker green: 95% of max or more

    /// <summary>One cell per stat code ("OQ" ...): its value (blank when the spawn doesn't have the stat) and background.</summary>
    public static Dictionary<string, StatCell> Cells(Spawn s, ResourceTree tree)
    {
        var node = tree.Find(s.ResourceTypeId);
        var pricing = Services.Settings.Pricing;
        var cells = new Dictionary<string, StatCell>();
        foreach (var stat in StatInfo.All)
        {
            if (!s.Stats.TryGetValue(stat, out var v)) { cells[stat.ToString()] = new StatCell("", Brushes.Transparent); continue; }
            int max = node?.Ranges != null && node.Ranges.TryGetValue(stat, out var range) ? range.Max : 0;
            int tier = StatQuality.Tier(v, max, pricing.HighThreshold, pricing.TopThreshold);
            cells[stat.ToString()] = new StatCell(v.ToString(), tier == 2 ? TopBrush : tier == 1 ? HighBrush : Brushes.Transparent);
        }
        return cells;
    }
}
