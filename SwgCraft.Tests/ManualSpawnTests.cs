using System.Globalization;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public sealed class ManualSpawnTests : IDisposable
{
    static readonly DateTimeOffset D1 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    readonly string _file = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.db");
    readonly SpawnStore _store;
    readonly ResourceTree _tree;
    readonly IGalaxySpawnStore _g;

    public ManualSpawnTests()
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
            Ranges = new()
            {
                [Stat.OQ] = new StatRange { Min = 300, Max = 800 },
                [Stat.UT] = new StatRange { Min = 1, Max = 1000 },
            },
        };
        var group = new ResourceNode { Id = "metal", Name = "Metal", Children = new() { leaf } };
        _tree = new ResourceTree(new ResourceTreeDocument { Nodes = { group } });
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + "*"))
            try { File.Delete(f); } catch (IOException) { }
    }

    static ManualSpawnInput Input(string name = "sipsyl", string type = "aluminum_agrinium", int oq = 500,
                                  DateTimeOffset? spawned = null, DateTimeOffset? despawned = null) =>
        new(name, type, new Dictionary<Stat, int> { [Stat.OQ] = oq }, spawned, despawned);

    static string FeedXml(string name, DateTimeOffset entered) =>
        $"<resources><resource><name>{name}</name><galaxy id=\"118\">Finalizer</galaxy>" +
        $"<enter_date>{entered.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)} +0000</enter_date>" +
        "<resource_type id=\"aluminum_agrinium\">T</resource_type><group_id>g</group_id>" +
        "<stats><OQ>600</OQ></stats><planets><planet>Corellia</planet></planets></resource></resources>";

    SyncOutcome Sync(string xml, int minute = 0) =>
        new FeedSyncService(_store, new HttpClient(), () => new AppSettings())
            .ApplyXml(118, xml, _tree, Now.AddMinutes(minute), false);

    [Fact]
    public void Add_creates_a_past_manual_spawn()
    {
        Assert.Empty(_g.CheckManualSpawn(Input("Sipsyl "), _tree).Errors);
        var id = _g.AddManualSpawn(Input("Sipsyl "));
        var s = Assert.Single(_g.Spawns(false, null));
        Assert.Equal(id, s.Id);
        Assert.Equal("sipsyl", s.Name);
        Assert.Equal(SpawnSources.Manual, s.Source);
        Assert.NotNull(s.DespawnedUtc);
        Assert.True(SpawnDates.IsUnknown(s.SpawnedUtc));
        Assert.Equal(500, s.Stats[Stat.OQ]);
        Assert.Equal(0, _g.CurrentCount());
    }

    [Fact]
    public void Known_dates_are_stored()
    {
        var id = _g.AddManualSpawn(Input(spawned: D1, despawned: D1.AddDays(10)));
        var s = _g.Spawns(false, null).Single(x => x.Id == id);
        Assert.Equal(D1, s.SpawnedUtc);
        Assert.Equal(D1.AddDays(10), s.DespawnedUtc);
    }

    [Fact]
    public void Check_rejects_bad_input()
    {
        Assert.NotEmpty(_g.CheckManualSpawn(Input("two words"), _tree).Errors);
        Assert.NotEmpty(_g.CheckManualSpawn(Input(""), _tree).Errors);
        Assert.NotEmpty(_g.CheckManualSpawn(Input(type: "nonexistent"), _tree).Errors);
        Assert.NotEmpty(_g.CheckManualSpawn(Input(type: "metal"), _tree).Errors);   // a group, not a leaf
        Assert.NotEmpty(_g.CheckManualSpawn(Input(oq: 1200), _tree).Errors);
        Assert.NotEmpty(_g.CheckManualSpawn(Input(spawned: D1, despawned: D1.AddDays(-1)), _tree).Errors);

        var noStats = new ManualSpawnInput("sipsyl", "aluminum_agrinium", new Dictionary<Stat, int>(), null, null);
        Assert.NotEmpty(_g.CheckManualSpawn(noStats, _tree).Errors);

        var wrongStat = new ManualSpawnInput("sipsyl", "aluminum_agrinium", new Dictionary<Stat, int> { [Stat.CR] = 5 }, null, null);
        Assert.NotEmpty(_g.CheckManualSpawn(wrongStat, _tree).Errors);
    }

    [Fact]
    public void Value_outside_the_type_range_is_only_a_warning()
    {
        var check = _g.CheckManualSpawn(Input(oq: 900), _tree);   // range is 300-800
        Assert.Empty(check.Errors);
        Assert.Single(check.Warnings);
    }

    [Fact]
    public void Existing_spawn_of_the_same_name_and_type_warns_unless_it_is_the_one_being_edited()
    {
        var id = _g.AddManualSpawn(Input());
        Assert.Single(_g.CheckManualSpawn(Input(), _tree).Warnings);
        Assert.Empty(_g.CheckManualSpawn(Input(), _tree, id).Warnings);
    }

    [Fact]
    public void Sync_ignores_manual_spawns_and_inventory_stays_put()
    {
        var manualId = _g.AddManualSpawn(Input("alpha"));
        _g.AdjustInventory(manualId, 25);

        var first = Sync(FeedXml("beta", D1));
        Assert.Equal(0, first.Result!.Despawned);

        // The feed later lists a spawn with the same name: it becomes a new row, the manual one stays.
        var second = Sync(FeedXml("alpha", D1.AddDays(1)), 60);
        Assert.Equal(1, second.Result!.Added);
        Assert.Equal(2, _g.Spawns(false, null).Count(s => s.Name == "alpha"));
        var item = Assert.Single(_g.Inventory());
        Assert.Equal(manualId, item.Spawn.Id);
        Assert.Equal(SpawnSources.Manual, item.Spawn.Source);
    }

    [Fact]
    public void Update_changes_manual_spawns_only()
    {
        var id = _g.AddManualSpawn(Input("alpha"));
        Assert.True(_g.UpdateManualSpawn(id, Input("alpha", oq: 700)));
        Assert.Equal(700, _g.Spawns(false, null).Single(s => s.Id == id).Stats[Stat.OQ]);

        Sync(FeedXml("gamma", D1));
        var feedId = _g.Spawns(false, null).Single(s => s.Name == "gamma").Id;
        Assert.False(_g.UpdateManualSpawn(feedId, Input("gamma", oq: 700)));
        Assert.Equal(600, _g.Spawns(false, null).Single(s => s.Id == feedId).Stats[Stat.OQ]);
    }

    [Fact]
    public void Delete_is_blocked_by_inventory_and_refused_for_feed_spawns()
    {
        var id = _g.AddManualSpawn(Input("alpha"));
        _g.AdjustInventory(id, 5);
        Assert.False(_g.DeleteManualSpawn(id));

        _g.SetInventory(id, 0, null);
        Assert.True(_g.DeleteManualSpawn(id));
        Assert.Empty(_g.Spawns(false, null));

        Sync(FeedXml("gamma", D1));
        Assert.False(_g.DeleteManualSpawn(_g.Spawns(false, null).Single().Id));
    }

    [Fact]
    public void Migration_adds_the_source_column_and_existing_rows_default_to_feed()
    {
        Sync(FeedXml("gamma", D1));
        Assert.Equal(SpawnSources.Feed, Assert.Single(_g.Spawns(false, null)).Source);
    }
}
