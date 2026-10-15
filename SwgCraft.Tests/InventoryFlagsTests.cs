using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public sealed class InventoryFlagsTests : IDisposable
{
    static readonly DateTimeOffset D1 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset D2 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    readonly string _file = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.db");
    readonly SpawnStore _store;
    readonly ResourceTree _tree;
    readonly IGalaxySpawnStore _g;

    public InventoryFlagsTests()
    {
        var db = new SpawnDatabase(_file);
        db.Migrate();
        _store = new SpawnStore(db);
        _store.UpsertGalaxy(118, "Finalizer");
        _g = _store.ForGalaxy(118);

        var leaf = new ResourceNode
        {
            Id = "aluminum_agrinium", Name = "Agrinium Aluminum",
            ExternalIds = new() { [ExternalSources.GalaxyHarvester] = "aluminum_agrinium" },
            Ranges = new() { [Stat.OQ] = new StatRange { Min = 1, Max = 1000 } },
        };
        _tree = new ResourceTree(new ResourceTreeDocument { Nodes = { leaf } });
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + "*"))
            try { File.Delete(f); } catch (IOException) { }
    }

    static FeedSpawn Feed(string name, DateTimeOffset entered, int oq = 500) =>
        new(name, 118, "Finalizer", entered, "aluminum_agrinium", "T",
            new Dictionary<Stat, int> { [Stat.OQ] = oq }, new[] { "Corellia" });

    void Apply(DateTimeOffset now, params FeedSpawn[] feed) =>
        _g.ApplyFeed(feed, new HashSet<string>(), _tree, now, new SyncOptions());

    Spawn Find(string name, bool current = true) =>
        _g.Spawns(false, null).Single(s => s.Name == name && (!current || s.DespawnedUtc == null));

    [Fact]
    public void Flags_default_to_off_and_can_be_toggled()
    {
        Apply(Now, Feed("alpha", D1));
        var id = Find("alpha").Id;
        Assert.False(Find("alpha").Filtered);
        Assert.False(Find("alpha").Generic);

        _g.SetFiltered(id, true);
        _g.SetGeneric(id, true);
        Assert.True(Find("alpha").Filtered);
        Assert.True(Find("alpha").Generic);

        _g.SetFiltered(id, false);
        Assert.False(Find("alpha").Filtered);
        Assert.True(Find("alpha").Generic);
    }

    [Fact]
    public void Flags_survive_later_syncs_of_the_same_spawn()
    {
        Apply(Now, Feed("alpha", D1));
        var id = Find("alpha").Id;
        _g.SetFiltered(id, true);
        _g.SetGeneric(id, true);

        Apply(Now.AddHours(1), Feed("alpha", D1, oq: 700));   // same spawn, stats corrected
        var s = Find("alpha");
        Assert.Equal(id, s.Id);
        Assert.True(s.Filtered);
        Assert.True(s.Generic);
    }

    [Fact]
    public void A_reused_name_starts_unflagged_and_the_old_spawn_keeps_its_flags()
    {
        Apply(Now, Feed("alpha", D1));
        var oldId = Find("alpha").Id;
        _g.SetFiltered(oldId, true);

        Apply(Now.AddHours(1), Feed("alpha", D2, oq: 300));   // same name, new spawn
        var current = Find("alpha");
        Assert.NotEqual(oldId, current.Id);
        Assert.False(current.Filtered);
        Assert.True(_g.Spawns(false, null).Single(s => s.Id == oldId).Filtered);
    }

    [Fact]
    public void Another_galaxy_cannot_change_flags()
    {
        Apply(Now, Feed("alpha", D1));
        var id = Find("alpha").Id;
        _store.UpsertGalaxy(119, "Other");
        _store.ForGalaxy(119).SetFiltered(id, true);
        Assert.False(Find("alpha").Filtered);
    }

    [Fact]
    public void New_stack_has_no_price_and_price_can_be_set_and_cleared()
    {
        Apply(Now, Feed("alpha", D1));
        var id = Find("alpha").Id;
        _g.AdjustInventory(id, 100);
        Assert.Null(Assert.Single(_g.Inventory()).PricePerUnit);

        _g.SetPrice(id, 2.35);
        Assert.Equal(2.35, Assert.Single(_g.Inventory()).PricePerUnit);

        _g.SetPrice(id, null);
        Assert.Null(Assert.Single(_g.Inventory()).PricePerUnit);
    }

    [Fact]
    public void Price_survives_quantity_changes()
    {
        Apply(Now, Feed("alpha", D1));
        var id = Find("alpha").Id;
        _g.AdjustInventory(id, 100);
        _g.SetPrice(id, 1.5);

        _g.AdjustInventory(id, 50);
        _g.AdjustInventory(id, -20);
        _g.SetInventory(id, 40, "note");
        var item = Assert.Single(_g.Inventory());
        Assert.Equal(40, item.Quantity);
        Assert.Equal(1.5, item.PricePerUnit);
    }

    [Fact]
    public void Setting_a_price_without_a_stack_does_nothing()
    {
        Apply(Now, Feed("alpha", D1));
        _g.SetPrice(Find("alpha").Id, 9.0);
        Assert.Empty(_g.Inventory());
    }

    [Fact]
    public void Default_calculator_leaves_the_price_blank()
    {
        Apply(Now, Feed("alpha", D1));
        var spawn = Find("alpha");
        Assert.Null(new BlankPriceCalculator().Calculate(spawn, _tree.Find(spawn.ResourceTypeId)));
    }
}
