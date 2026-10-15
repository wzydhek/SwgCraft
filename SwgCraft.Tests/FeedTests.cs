using System.Text;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public class FeedParserTests
{
    // Record layout taken from a real Galaxy Harvester export.
    const string Sample = """
<resources>
<resource>
<name>eliide</name>
<galaxy id="118">Finalizer</galaxy>
<enter_date>Sun, 27 Sep 2026 04:21:31 -0800</enter_date>
<resource_type id="aluminum_agrinium">Agrinium Aluminum</resource_type>
<group_id>aluminum</group_id>
<stats>
<CR>244</CR>
<CD>417</CD>
<DR>148</DR>
<HR>338</HR>
<MA>434</MA>
<OQ>622</OQ>
<SR>491</SR>
<UT>520</UT>
</stats>
<planets>
<planet>Corellia</planet>
<planet>Dantooine</planet>
<planet>Dathomir</planet>
<planet>Endor</planet>
<planet>Lok</planet>
<planet>Naboo</planet>
<planet>Talus</planet>
<planet>Tatooine</planet>
</planets>
</resource>
</resources>
""";

    [Fact]
    public void Sample_record_parses_and_translates_ma_to_mb()
    {
        var res = GhFeedParser.Parse(Sample, 118);
        Assert.Null(res.FatalError);
        var s = Assert.Single(res.Spawns);
        Assert.Equal("eliide", s.Name);
        Assert.Equal("aluminum_agrinium", s.TypeExternalId);
        Assert.Equal("Finalizer", res.GalaxyName);
        Assert.Equal(434, s.Stats[Stat.MB]);
        Assert.Equal(244, s.Stats[Stat.CR]);
        Assert.False(s.Stats.ContainsKey(Stat.ER));
        Assert.Equal(8, s.Planets.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 12, 21, 31, TimeSpan.Zero), s.EnterDate);
    }

    [Fact]
    public void Unknown_stat_rejects_only_that_record_but_keeps_the_name_alive()
    {
        var res = GhFeedParser.Parse(Sample.Replace("<OQ>622</OQ>", "<ZZ>5</ZZ>"), 118);
        Assert.Empty(res.Spawns);
        Assert.Contains("eliide", res.RejectedNames);
    }

    [Fact]
    public void Galaxy_mismatch_is_fatal() =>
        Assert.NotNull(GhFeedParser.Parse(Sample, 999).FatalError);

    [Fact]
    public void Invalid_xml_is_fatal() =>
        Assert.NotNull(GhFeedParser.Parse("<resources><resource>", 118).FatalError);
}

public sealed class SyncTests : IDisposable
{
    const string D1 = "Sun, 27 Sep 2026 04:21:31 -0800";
    const string D2 = "Mon, 28 Sep 2026 04:21:31 -0800";
    static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    readonly string _file = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.db");
    readonly SpawnStore _store;
    readonly FeedSyncService _svc;
    readonly ResourceTree _tree;

    public SyncTests()
    {
        var db = new SpawnDatabase(_file);
        db.Migrate();
        _store = new SpawnStore(db);
        _svc = new FeedSyncService(_store, new HttpClient(), () => new AppSettings());

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

    static string Feed(params (string Name, string Type, string Date, int Oq)[] items)
    {
        var sb = new StringBuilder("<resources>");
        foreach (var i in items)
            sb.Append($"<resource><name>{i.Name}</name><galaxy id=\"118\">Finalizer</galaxy><enter_date>{i.Date}</enter_date>" +
                      $"<resource_type id=\"{i.Type}\">T</resource_type><group_id>g</group_id><stats><OQ>{i.Oq}</OQ></stats>" +
                      "<planets><planet>Corellia</planet></planets></resource>");
        return sb.Append("</resources>").ToString();
    }

    SyncOutcome Sync(string xml, int minute = 0) => _svc.ApplyXml(118, xml, _tree, T0.AddMinutes(minute), false);
    IReadOnlyList<Spawn> All() => _store.ForGalaxy(118).Spawns(false, null);

    [Fact]
    public void First_sync_adds_spawns_and_creates_the_galaxy()
    {
        var o = Sync(Feed(("alpha", "aluminum_agrinium", D1, 500)));
        Assert.Equal(SyncState.Completed, o.State);
        Assert.Equal(1, o.Result!.Added);
        Assert.Single(_store.Galaxies());
        Assert.Equal("Corellia", All().Single().Planets);
    }

    [Fact]
    public void Identical_feed_twice_changes_nothing()
    {
        var xml = Feed(("alpha", "aluminum_agrinium", D1, 500));
        Sync(xml);
        var o = Sync(xml, 60);
        Assert.Equal(0, o.Result!.Added + o.Result.Updated + o.Result.Despawned + o.Result.Replaced + o.Result.Reopened);
        Assert.Single(All());
    }

    [Fact]
    public void Missing_spawn_despawns_and_reopens_when_it_returns()
    {
        var a = ("alpha", "aluminum_agrinium", D1, 500);
        var b = ("beta", "aluminum_agrinium", D1, 400);
        Sync(Feed(a, b));
        Assert.Equal(1, Sync(Feed(a), 60).Result!.Despawned);
        Assert.Equal(1, _store.ForGalaxy(118).CurrentCount());
        var back = Sync(Feed(a, b), 120).Result!;
        Assert.Equal(1, back.Reopened);
        Assert.Equal(0, back.Added);
        Assert.Equal(2, All().Count);
    }

    [Fact]
    public void Reused_name_with_a_new_spawn_date_closes_the_old_row()
    {
        Sync(Feed(("alpha", "aluminum_agrinium", D1, 500)));
        var o = Sync(Feed(("alpha", "aluminum_agrinium", D2, 300)), 60).Result!;
        Assert.Equal(1, o.Replaced);
        Assert.Equal(2, All().Count);
        Assert.Single(All(), s => s.DespawnedUtc == null);
    }

    [Fact]
    public void Same_spawn_with_changed_stats_is_corrected_in_place()
    {
        Sync(Feed(("alpha", "aluminum_agrinium", D1, 500)));
        var o = Sync(Feed(("alpha", "aluminum_agrinium", D1, 700)), 60).Result!;
        Assert.Equal(1, o.Updated);
        var only = Assert.Single(All());
        Assert.Equal(700, only.Stats[Stat.OQ]);
    }

    [Fact]
    public void Empty_feed_aborts_and_despawns_nothing()
    {
        Sync(Feed(("alpha", "aluminum_agrinium", D1, 500)));
        var o = Sync("<resources/>", 60);
        Assert.Equal(SyncState.Aborted, o.State);
        Assert.Equal(1, _store.ForGalaxy(118).CurrentCount());
    }

    [Fact]
    public void Unmapped_type_is_counted_not_inserted()
    {
        var o = Sync(Feed(("alpha", "mystery_type", D1, 500)));
        Assert.Equal(1, o.Result!.Unmapped);
        Assert.Empty(All());
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        new SpawnDatabase(_file).Migrate();
        new SpawnDatabase(_file).Migrate();
        Assert.Empty(All());
    }
}
