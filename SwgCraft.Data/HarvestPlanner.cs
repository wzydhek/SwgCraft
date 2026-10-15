using SwgCraft.Core;

namespace SwgCraft.Data;

/// <summary>An enabled schematic modifier that wants a resource with a particular stat mix.</summary>
public sealed record HarvestUse(string SchematicId, string SchematicName, string Experimental, string Modifier);

/// <summary>A currently spawning resource scored for one need.</summary>
public sealed record HarvestCandidate(Spawn Spawn, double Value, double? CapPercent, int DaysUp);

/// <summary>The best inventory stack for one need.</summary>
public sealed record HarvestStock(InventoryItem Item, double Value, double? CapPercent);

/// <summary>
/// One line of "What to Harvest": a resource (tree node; a group means any type below it) wanted with a particular stat mix,
/// the enabled schematics that want it, the current spawns ranked best first, and the best stack already in inventory.
/// </summary>
public sealed record HarvestNeed(
    string ResourceId, IReadOnlyDictionary<Stat, int> Weights, string WeightsText,
    IReadOnlyList<HarvestUse> Uses, IReadOnlyList<HarvestCandidate> Candidates, HarvestStock? BestStock)
{
    public HarvestCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>How much better the best current spawn is than the best stack held (the whole value when nothing is held); null when nothing spawns.</summary>
    public double? Gain => Best == null ? null : Best.Value - (BestStock?.Value ?? 0);

    /// <summary>True when a current spawn is better than anything in inventory for this need.</summary>
    public bool BeatsInventory => Best != null && (BestStock == null || Best.Value > BestStock.Value);
}

/// <summary>
/// Works out what is worth harvesting now (like JnF's "What to Harvest" sheet):
/// <list type="number">
/// <item>Takes the schematics of the enabled professions (skipping those whose experimentation is ignored).</item>
/// <item>Pairs each resource slot with every experimentation modifier's stat percentages: one need per resource + stat mix,
///       e.g. "Steel - OQ 67% UT 33%", listing every schematic modifier that shares it.</item>
/// <item>Scores every current spawn of that resource (or any type below a group) by the weighted average of its stats,
///       out of 1000 - the number experimentation uses - and finds the best one.</item>
/// <item>Scores the inventory the same way, so the list can show whether today's best beats what is already held.</item>
/// </list>
/// Spawns marked as filtered ("skip in builds") are left out, both spawning and in inventory.
/// </summary>
public static class HarvestPlanner
{
    public static List<HarvestNeed> Plan(
        IEnumerable<Schematic> schematics, Func<string, bool> professionEnabled, ResourceTree tree,
        IEnumerable<Spawn> currentSpawns, IEnumerable<InventoryItem> inventory, DateTimeOffset now, int topCandidates = 10)
    {
        // 1-2: needs
        var needs = new Dictionary<string, (string Resource, SortedDictionary<Stat, int> Weights, List<HarvestUse> Uses)>(StringComparer.Ordinal);
        foreach (var s in schematics)
        {
            if (s.IgnoreExperimentation || s.Experiments.Count == 0) continue;
            if (!s.Professions.Any(p => !string.IsNullOrWhiteSpace(p) && professionEnabled(p.Trim()))) continue;

            var resources = s.Resources.Select(r => r.Resource?.Trim() ?? "").Where(r => r.Length > 0)
                                       .Distinct(StringComparer.Ordinal).ToList();
            var name = string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name.Trim();
            foreach (var e in s.Experiments)
                foreach (var m in e.Modifiers)
                {
                    var w = Normalize(m.Weights);
                    if (w.Count == 0) continue;
                    var wKey = WeightsText(w);
                    foreach (var res in resources)
                    {
                        var key = res + "|" + wKey;
                        if (!needs.TryGetValue(key, out var n)) needs[key] = n = (res, w, new List<HarvestUse>());
                        var use = new HarvestUse(s.Id, name, e.Name?.Trim() ?? "", m.Name?.Trim() ?? "");
                        if (!n.Uses.Contains(use)) n.Uses.Add(use);
                    }
                }
        }

        var spawns = currentSpawns.Where(sp => sp.DespawnedUtc == null && !sp.Filtered).ToList();
        var stock = inventory.Where(i => i.Quantity > 0 && !i.Spawn.Filtered).ToList();

        // 3-4: score
        var result = new List<HarvestNeed>(needs.Count);
        foreach (var (res, weights, uses) in needs.Values)
        {
            var candidates = spawns.Where(sp => tree.IsDescendantOrSelf(sp.ResourceTypeId, res))
                .Select(sp => new HarvestCandidate(sp, Score(sp.Stats, weights), CapPercent(sp, weights, tree),
                                                   Math.Max(0, (int)(now - sp.SpawnedUtc).TotalDays)))
                .OrderByDescending(c => c.Value).ThenBy(c => c.Spawn.Name, StringComparer.Ordinal)
                .Take(Math.Max(1, topCandidates))
                .ToList();

            var best = stock.Where(i => tree.IsDescendantOrSelf(i.Spawn.ResourceTypeId, res))
                .Select(i => new HarvestStock(i, Score(i.Spawn.Stats, weights), CapPercent(i.Spawn, weights, tree)))
                .OrderByDescending(x => x.Value).FirstOrDefault();

            uses.Sort((a, b) => string.Compare(a.SchematicName, b.SchematicName, StringComparison.OrdinalIgnoreCase));
            result.Add(new HarvestNeed(res, weights, WeightsText(weights), uses, candidates, best));
        }
        return result.OrderBy(n => n.ResourceId, StringComparer.Ordinal).ThenBy(n => n.WeightsText, StringComparer.Ordinal).ToList();
    }

    /// <summary>Weighted average of the stats (a missing stat counts as 0), on the game's 0-1000 scale.</summary>
    public static double Score(IReadOnlyDictionary<Stat, int> stats, IReadOnlyDictionary<Stat, int> weights)
    {
        double sum = 0, total = 0;
        foreach (var (stat, w) in weights)
        {
            if (w <= 0) continue;
            total += w;
            sum += w * (stats.TryGetValue(stat, out var v) ? v : 0);
        }
        return total == 0 ? 0 : sum / total;
    }

    /// <summary>
    /// The score as a share of the best that the spawn's resource type can ever reach (0-100), or null when the type's
    /// ranges aren't known for every weighted stat.
    /// </summary>
    public static double? CapPercent(Spawn spawn, IReadOnlyDictionary<Stat, int> weights, ResourceTree tree)
    {
        var ranges = tree.Find(spawn.ResourceTypeId)?.Ranges;
        if (ranges == null) return null;
        double sum = 0, cap = 0;
        foreach (var (stat, w) in weights)
        {
            if (w <= 0) continue;
            if (!ranges.TryGetValue(stat, out var r) || r.Max <= 0) return null;
            cap += w * r.Max;
            sum += w * (spawn.Stats.TryGetValue(stat, out var v) ? v : 0);
        }
        return cap == 0 ? null : 100.0 * sum / cap;
    }

    /// <summary>"OQ 67% UT 33%": biggest share first, then stat order.</summary>
    public static string WeightsText(IReadOnlyDictionary<Stat, int> weights) =>
        string.Join(" ", weights.Where(kv => kv.Value > 0)
                                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                                .Select(kv => $"{kv.Key} {kv.Value}%"));

    static SortedDictionary<Stat, int> Normalize(IReadOnlyDictionary<Stat, int>? weights)
    {
        var w = new SortedDictionary<Stat, int>();
        if (weights != null)
            foreach (var (stat, pct) in weights)
                if (pct > 0) w[stat] = pct;
        return w;
    }
}
