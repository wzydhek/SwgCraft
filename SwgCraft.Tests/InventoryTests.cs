using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public sealed class InventoryTests : IDisposable
{
    static readonly DateTimeOffset D1 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset D2 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    readonly string _file = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.db");
    readonly SpawnStore _store;
    readonly ResourceTree _tree;
    readonly IGalaxySpawnStore _g;

    public InventoryTests()
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

    static FeedSpawn Spawn(string name, DateTimeOffset entered, int oq) =>
        new(name, 118, "Finalizer", entered, "aluminum_agrinium", "T",
            new Dictionary<Stat, int> { [Stat.OQ] = oq }, new[] { "Corellia" });

    void Apply(DateTimeOffset now, params FeedSpawn[] feed) =>
        _g.ApplyFeed(feed, new HashSet<string>(), _tree, now, new SyncOptions());

    long IdOf(string name) => _g.Spawns(false, null).First(s => s.Name == name).Id;

    [Fact]
    public void Adjust_adds_subtracts_and_removes_at_zero()
    {
        Apply(Now, Spawn("alpha", D1, 500));
        var id = IdOf("alpha");
        Assert.Equal((int?)100, _g.AdjustInventory(id, 100));
        Assert.Equal((int?)60, _g.AdjustInventory(id, -40));
        Assert.Equal(60, Assert.Single(_g.Inventory()).Quantity);
        Assert.Equal((int?)0, _g.AdjustInventory(id, -60));
        Assert.Empty(_g.Inventory());
    }

    [Fact]
    public void Using_more_than_on_hand_changes_nothing()
    {
        Apply(Now, Spawn("alpha", D1, 500));
        var id = IdOf("alpha");
        Assert.Null(_g.AdjustInventory(id, -1));
        _g.AdjustInventory(id, 5);
        Assert.Null(_g.AdjustInventory(id, -6));
        Assert.Equal(5, Assert.Single(_g.Inventory()).Quantity);
    }

    [Fact]
    public void Set_writes_the_note_exactly_and_adjust_keeps_it()
    {
        Apply(Now, Spawn("alpha", D1, 500));
        var id = IdOf("alpha");
        _g.SetInventory(id, 10, "from Dantooine");
        _g.AdjustInventory(id, 5);                       // no note given: keep the existing one
        Assert.Equal("from Dantooine", Assert.Single(_g.Inventory()).Note);
        _g.SetInventory(id, 15, null);                   // explicit clear
        Assert.Null(Assert.Single(_g.Inventory()).Note);
    }

    [Fact]
    public void Stack_survives_the_spawn_despawning()
    {
        Apply(Now, Spawn("alpha", D1, 500), Spawn("beta", D1, 400));
        _g.AdjustInventory(IdOf("alpha"), 10);
        Apply(Now.AddHours(1), Spawn("beta", D1, 400));   // alpha despawns
        var item = Assert.Single(_g.Inventory());
        Assert.Equal("alpha", item.Spawn.Name);
        Assert.NotNull(item.Spawn.DespawnedUtc);
    }

    [Fact]
    public void Stack_stays_on_the_old_spawn_when_a_name_is_reused()
    {
        Apply(Now, Spawn("alpha", D1, 500));
        var oldId = IdOf("alpha");
        _g.AdjustInventory(oldId, 10);
        Apply(Now.AddHours(1), Spawn("alpha", D2, 300));  // same name, new spawn
        var item = Assert.Single(_g.Inventory());
        Assert.Equal(oldId, item.Spawn.Id);
        Assert.Equal(500, item.Spawn.Stats[Stat.OQ]);
        Assert.NotNull(item.Spawn.DespawnedUtc);
    }

    [Fact]
    public void Another_galaxy_cannot_hold_this_spawn()
    {
        Apply(Now, Spawn("alpha", D1, 500));
        var id = IdOf("alpha");
        _store.UpsertGalaxy(119, "Other");
        Assert.ThrowsAny<Exception>(() => _store.ForGalaxy(119).AdjustInventory(id, 5));
        Assert.Empty(_store.ForGalaxy(119).Inventory());
    }
}
