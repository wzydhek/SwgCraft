using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public sealed class HarvestPlannerTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // metal (group) > steel (leaf, OQ/UT caps 1000), iron (leaf, OQ cap 500, UT cap 1000)
    static readonly ResourceTree Tree = new(new ResourceTreeDocument
    {
        Nodes =
        {
            new ResourceNode
            {
                Id = "metal", Name = "Metal",
                Children = new()
                {
                    new ResourceNode { Id = "steel", Name = "Steel", Ranges = new() { [Stat.OQ] = new() { Min = 1, Max = 1000 }, [Stat.UT] = new() { Min = 1, Max = 1000 } } },
                    new ResourceNode { Id = "iron", Name = "Iron", Ranges = new() { [Stat.OQ] = new() { Min = 1, Max = 500 }, [Stat.UT] = new() { Min = 1, Max = 1000 } } },
                },
            },
        },
    });

    static Spawn S(long id, string name, string type, int oq, int ut, int daysAgo = 2, bool despawned = false, bool filtered = false) =>
        new(id, 118, name, type, new Dictionary<Stat, int> { [Stat.OQ] = oq, [Stat.UT] = ut },
            Now.AddDays(-daysAgo), Now.AddDays(-daysAgo), Now, despawned ? Now : null, "Naboo, Tatooine", Filtered: filtered);

    static Schematic Sch(string id, string prof, string resource, params ExperimentModifier[] mods)
    {
        var s = new Schematic { Id = id, Name = id, Professions = { prof } };
        s.Resources.Add(new ResourceSlot { Resource = resource, Amount = 10 });
        if (mods.Length > 0) s.Experiments.Add(new Experiment { Name = "Experimental Quality", Modifiers = mods.ToList() });
        return s;
    }

    static ExperimentModifier M(string name, params (Stat, int)[] w)
    {
        var m = new ExperimentModifier { Name = name };
        foreach (var (s, p) in w) m.Weights[s] = p;
        return m;
    }

    static List<HarvestNeed> Plan(IEnumerable<Schematic> schematics, IEnumerable<Spawn> spawns, IEnumerable<InventoryItem>? inv = null,
                                  Func<string, bool>? enabled = null) =>
        HarvestPlanner.Plan(schematics, enabled ?? (_ => true), Tree, spawns, inv ?? Array.Empty<InventoryItem>(), Now);

    [Fact]
    public void Score_is_the_weighted_average_on_the_game_scale()
    {
        var stats = new Dictionary<Stat, int> { [Stat.OQ] = 900, [Stat.UT] = 600 };
        Assert.Equal(801, HarvestPlanner.Score(stats, new Dictionary<Stat, int> { [Stat.OQ] = 67, [Stat.UT] = 33 }), 6);
        Assert.Equal(0, HarvestPlanner.Score(stats, new Dictionary<Stat, int> { [Stat.CD] = 100 }));   // a missing stat counts as 0
    }

    [Fact]
    public void One_line_per_resource_and_stat_mix_shared_by_every_schematic_that_wants_it()
    {
        var a = Sch("rifle", "ws", "steel", M("Quality", (Stat.OQ, 67), (Stat.UT, 33)));
        var b = Sch("pistol", "ws", "steel", M("Quality", (Stat.OQ, 67), (Stat.UT, 33)), M("Range", (Stat.UT, 100)));
        var needs = Plan(new[] { a, b }, Array.Empty<Spawn>());
        Assert.Equal(2, needs.Count);
        var shared = needs.Single(n => n.WeightsText == "OQ 67% UT 33%");
        Assert.Equal(new[] { "pistol", "rifle" }, shared.Uses.Select(u => u.SchematicName).ToArray());
        Assert.Single(needs.Single(n => n.WeightsText == "UT 100%").Uses);
    }

    [Fact]
    public void Disabled_professions_and_ignored_or_missing_experimentation_add_nothing()
    {
        var off = Sch("off", "chef", "steel", M("Q", (Stat.OQ, 100)));
        var ignored = Sch("chair", "ws", "steel", M("Q", (Stat.OQ, 100)));
        ignored.IgnoreExperimentation = true;
        var plain = Sch("plain", "ws", "steel");
        Assert.Empty(Plan(new[] { off, ignored, plain }, Array.Empty<Spawn>(), enabled: p => p != "chef"));
    }

    [Fact]
    public void A_group_slot_considers_every_type_below_it_and_the_best_value_wins()
    {
        var s = Sch("rifle", "ws", "metal", M("Q", (Stat.OQ, 100)));
        var spawns = new[]
        {
            S(1, "steelone", "steel", oq: 700, ut: 100),
            S(2, "ironone", "iron", oq: 480, ut: 900),
            S(3, "steelgone", "steel", oq: 999, ut: 999, despawned: true),
            S(4, "steelskip", "steel", oq: 990, ut: 999, filtered: true),
        };
        var need = Assert.Single(Plan(new[] { s }, spawns));
        Assert.Equal(new[] { "steelone", "ironone" }, need.Candidates.Select(c => c.Spawn.Name).ToArray());
        Assert.Equal(700, need.Best!.Value);
        Assert.Equal(70, need.Best.CapPercent!.Value, 6);
        Assert.Equal(96, need.Candidates[1].CapPercent!.Value, 6);   // iron 480 of a 500 cap: rarer, but lower value
        Assert.Equal(2, need.Best.DaysUp);
    }

    [Fact]
    public void Inventory_is_compared_and_gain_is_reported()
    {
        var s = Sch("rifle", "ws", "steel", M("Q", (Stat.OQ, 100)));
        var held = new InventoryItem(1, S(9, "oldsteel", "steel", oq: 650, ut: 1, despawned: true), 5000, null, Now);
        var junk = new InventoryItem(2, S(8, "badsteel", "steel", oq: 990, ut: 1, despawned: true, filtered: true), 10, null, Now);

        var better = Assert.Single(Plan(new[] { s }, new[] { S(1, "newsteel", "steel", 800, 1) }, new[] { held, junk }));
        Assert.Equal("oldsteel", better.BestStock!.Item.Spawn.Name);   // the filtered stack is ignored
        Assert.Equal(150, better.Gain!.Value, 6);
        Assert.True(better.BeatsInventory);

        var worse = Assert.Single(Plan(new[] { s }, new[] { S(1, "meh", "steel", 600, 1) }, new[] { held }));
        Assert.False(worse.BeatsInventory);
        Assert.Equal(-50, worse.Gain!.Value, 6);

        var nothing = Assert.Single(Plan(new[] { s }, Array.Empty<Spawn>(), new[] { held }));
        Assert.Null(nothing.Best);
        Assert.Null(nothing.Gain);
        Assert.False(nothing.BeatsInventory);
    }
}
