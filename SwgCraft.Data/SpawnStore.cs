using System.Text;
using Microsoft.Data.Sqlite;
using SwgCraft.Core;

namespace SwgCraft.Data;

public interface ISpawnStore
{
    IReadOnlyList<Galaxy> Galaxies();
    void UpsertGalaxy(int id, string name);
    /// <summary>Everything that touches spawns or inventory goes through a galaxy-scoped store, so queries cannot leak across galaxies.</summary>
    IGalaxySpawnStore ForGalaxy(int galaxyId);
}

public interface IGalaxySpawnStore
{
    int GalaxyId { get; }
    IReadOnlyList<Spawn> Spawns(bool currentOnly, string? nameContains);
    int CurrentCount();
    DateTimeOffset? LastSuccessfulSync();
    IReadOnlyList<SyncLogEntry> RecentSyncs(int count = 20);
    void LogSync(string status, string message);
    SyncResult ApplyFeed(IReadOnlyList<FeedSpawn> feed, IReadOnlySet<string> rejectedNames,
                         ResourceTree tree, DateTimeOffset now, SyncOptions options);
    IReadOnlyList<InventoryItem> Inventory();
    int? AdjustInventory(long spawnId, int delta, string? note = null);
    void SetInventory(long spawnId, int quantity, string? note);

    ManualSpawnCheck CheckManualSpawn(ManualSpawnInput input, ResourceTree tree, long? excludeSpawnId = null);
    long AddManualSpawn(ManualSpawnInput input);
    bool UpdateManualSpawn(long spawnId, ManualSpawnInput input);
    bool DeleteManualSpawn(long spawnId);

    void SetPrice(long spawnId, double? pricePerUnit);
    void SetFiltered(long spawnId, bool value);
    void SetGeneric(long spawnId, bool value);

    // looted / crafted item inventory (ItemInventory.cs)
    IReadOnlyList<ItemStack> ItemInventory();
    int? AdjustItem(MemberKind kind, string itemId, string? serial, int delta, string? note = null);
    ItemStackUpdate UpdateItemStack(long stackId, string? serial, int quantity, string? note);
    bool DeleteItemStack(long stackId);
}

public sealed class SpawnStore : ISpawnStore
{
    readonly SpawnDatabase _db;
    public SpawnStore(SpawnDatabase db) => _db = db;

    public IReadOnlyList<Galaxy> Galaxies()
    {
        using var c = _db.Open();
        using var cmd = Sql.Cmd(c, null, "SELECT id, name, source FROM galaxy ORDER BY name");
        using var r = cmd.ExecuteReader();
        var list = new List<Galaxy>();
        while (r.Read()) list.Add(new Galaxy(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public void UpsertGalaxy(int id, string name)
    {
        using var c = _db.Open();
        Sql.Exec(c, null,
            "INSERT INTO galaxy (id, name) VALUES (@id, @name) ON CONFLICT(id) DO UPDATE SET name = excluded.name",
            ("@id", id), ("@name", name));
    }

    public IGalaxySpawnStore ForGalaxy(int galaxyId) => new GalaxySpawnStore(_db, galaxyId);
}

internal sealed partial class GalaxySpawnStore : IGalaxySpawnStore
{
    static readonly string[] StatCols = StatInfo.All.Select(s => s.ToString().ToLowerInvariant()).ToArray();
    static readonly string StatColList = string.Join(", ", StatCols);
    static readonly string StatParamList = string.Join(", ", StatCols.Select(c => "@" + c));
    static readonly string StatSetList = string.Join(", ", StatCols.Select(c => $"{c} = @{c}"));

    sealed record Existing(long Id, string TypeId, string SpawnedUtc, Dictionary<Stat, int> Stats);

    readonly SpawnDatabase _db;
    public int GalaxyId { get; }

    public GalaxySpawnStore(SpawnDatabase db, int galaxyId) { _db = db; GalaxyId = galaxyId; }

    // ---------------------------------------------------------------- queries

    public IReadOnlyList<Spawn> Spawns(bool currentOnly, string? nameContains)
    {
        using var c = _db.Open();
        var sql = new StringBuilder($"SELECT {SpawnColumns} FROM spawn s WHERE s.galaxy_id = @g");
        var ps = new List<(string, object?)> { ("@g", GalaxyId) };
        if (currentOnly) sql.Append(" AND s.despawned_utc IS NULL");
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            sql.Append(" AND s.name LIKE @n ESCAPE '\\'");
            var term = nameContains.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            ps.Add(("@n", "%" + term + "%"));
        }
        sql.Append(" ORDER BY s.spawned_utc DESC, s.name");

        using var cmd = Sql.Cmd(c, null, sql.ToString(), ps.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<Spawn>();
        while (r.Read()) list.Add(ReadSpawn(r, 0));
        return list;
    }

    public int CurrentCount()
    {
        using var c = _db.Open();
        return Convert.ToInt32(Sql.Scalar(c, null,
            "SELECT COUNT(*) FROM spawn WHERE galaxy_id = @g AND despawned_utc IS NULL", ("@g", GalaxyId)));
    }

    public DateTimeOffset? LastSuccessfulSync()
    {
        using var c = _db.Open();
        var v = Sql.Scalar(c, null,
            "SELECT MAX(finished_utc) FROM sync_log WHERE galaxy_id = @g AND status = 'ok'", ("@g", GalaxyId));
        return v is string s ? DbTime.FromDb(s) : null;
    }

    public IReadOnlyList<SyncLogEntry> RecentSyncs(int count = 20)
    {
        using var c = _db.Open();
        using var cmd = Sql.Cmd(c, null,
            "SELECT finished_utc, status, spawns_added, spawns_updated, spawns_reopened, spawns_replaced, " +
            "spawns_despawned, unmapped_count, rejected_count, message FROM sync_log " +
            "WHERE galaxy_id = @g ORDER BY id DESC LIMIT @n", ("@g", GalaxyId), ("@n", count));
        using var r = cmd.ExecuteReader();
        var list = new List<SyncLogEntry>();
        while (r.Read())
            list.Add(new SyncLogEntry(DbTime.FromDb(r.GetString(0)), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }

    public void LogSync(string status, string message) => WriteLog(DateTimeOffset.UtcNow, status, null, message);

    // ---------------------------------------------------------------- sync

    public SyncResult ApplyFeed(IReadOnlyList<FeedSpawn> feed, IReadOnlySet<string> rejectedNames,
                                ResourceTree tree, DateTimeOffset now, SyncOptions options)
    {
        var msgs = new List<string>();
        int added = 0, updated = 0, reopened = 0, replaced = 0, despawned = 0, unmapped = 0, outOfRange = 0;
        string status = "ok";
        var nowText = DbTime.ToDb(now);

        try
        {
            using var conn = _db.Open();
            var abort = SanityCheck(conn, feed.Count, options);
            if (abort != null)
            {
                status = "aborted";
                msgs.Add(abort);
            }
            else
            {
                using var tx = conn.BeginTransaction();
                var seen = new HashSet<string>(rejectedNames, StringComparer.Ordinal);

                foreach (var f in feed)
                {
                    seen.Add(f.Name);

                    if (!tree.TryResolveExternal(ExternalSources.GalaxyHarvester, f.TypeExternalId, out var node) || !node.IsLeaf)
                    {
                        unmapped++;
                        msgs.Add($"Unmapped resource type '{f.TypeExternalId}' ({f.TypeDisplayName}) on spawn '{f.Name}' - skipped.");
                        continue;
                    }

                    outOfRange += CheckRanges(f, node, msgs);
                    var spawnedText = DbTime.ToDb(f.EnterDate);
                    var cur = Find(conn, tx, f.Name, "despawned_utc IS NULL");

                    if (cur != null)
                    {
                        if (cur.TypeId == node.Id && cur.SpawnedUtc == spawnedText)
                        {
                            // Same spawn. Stats differing means the source corrected them; keep the row so inventory stays attached.
                            bool changed = false;
                            if (!SameStats(cur.Stats, f.Stats))
                            {
                                UpdateStats(conn, tx, cur.Id, f.Stats, nowText);
                                msgs.Add($"{f.Name}: stats were corrected by the source.");
                                changed = true;
                            }
                            else Sql.Exec(conn, tx, "UPDATE spawn SET last_seen_utc = @now WHERE id = @id", ("@now", nowText), ("@id", cur.Id));
                            if (SyncPlanets(conn, tx, cur.Id, f.Planets, nowText)) changed = true;
                            if (changed) updated++;
                        }
                        else
                        {
                            // Same name, different spawn: close the old row (inventory stays on it) and start a new one.
                            Sql.Exec(conn, tx, "UPDATE spawn SET despawned_utc = @now WHERE id = @id", ("@now", nowText), ("@id", cur.Id));
                            var id = Insert(conn, tx, f, node.Id, spawnedText, nowText);
                            SyncPlanets(conn, tx, id, f.Planets, nowText);
                            replaced++;
                            msgs.Add($"{f.Name}: name reused for a different spawn (type or spawn date changed); previous spawn closed.");
                        }
                    }
                    else
                    {
                        var old = Find(conn, tx, f.Name,
                            "despawned_utc IS NOT NULL AND resource_type_id = @t AND spawned_utc = @s",
                            ("@t", node.Id), ("@s", spawnedText));
                        if (old != null)
                        {
                            Sql.Exec(conn, tx, "UPDATE spawn SET despawned_utc = NULL, last_seen_utc = @now WHERE id = @id",
                                ("@now", nowText), ("@id", old.Id));
                            if (!SameStats(old.Stats, f.Stats)) UpdateStats(conn, tx, old.Id, f.Stats, nowText);
                            SyncPlanets(conn, tx, old.Id, f.Planets, nowText);
                            reopened++;
                        }
                        else
                        {
                            var id = Insert(conn, tx, f, node.Id, spawnedText, nowText);
                            SyncPlanets(conn, tx, id, f.Planets, nowText);
                            added++;
                        }
                    }
                }

                // Anything current that the feed no longer lists has despawned.
                var toClose = new List<long>();
                using (var cmd = Sql.Cmd(conn, tx, "SELECT id, name FROM spawn WHERE galaxy_id = @g AND despawned_utc IS NULL", ("@g", GalaxyId)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (!seen.Contains(r.GetString(1))) toClose.Add(r.GetInt64(0));
                foreach (var id in toClose)
                    Sql.Exec(conn, tx, "UPDATE spawn SET despawned_utc = @now WHERE id = @id", ("@now", nowText), ("@id", id));
                despawned = toClose.Count;

                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            status = "failed";
            msgs.Add("Sync failed and was rolled back: " + ex.Message);
            added = updated = reopened = replaced = despawned = unmapped = outOfRange = 0;
        }

        var result = new SyncResult(status, added, updated, reopened, replaced, despawned, unmapped,
                                    rejectedNames.Count, outOfRange, msgs);
        WriteLog(now, status, result, string.Join("\n", msgs.Take(20)) + (msgs.Count > 20 ? $"\n(+{msgs.Count - 20} more)" : ""));
        return result;
    }

    string? SanityCheck(SqliteConnection c, int feedCount, SyncOptions o)
    {
        if (o.AllowLargeDrop) return null;
        var cur = Convert.ToInt64(Sql.Scalar(c, null,
            "SELECT COUNT(*) FROM spawn WHERE galaxy_id = @g AND despawned_utc IS NULL", ("@g", GalaxyId)));
        if (cur == 0) return null;
        if (feedCount == 0)
            return $"The feed lists no resources but {cur} spawns are currently active. Nothing was changed.";
        if (cur >= 10 && feedCount * 100L < cur * o.MinFeedPercentOfPrevious)
            return $"The feed lists {feedCount} resources versus {cur} currently active (below {o.MinFeedPercentOfPrevious}%). Nothing was changed.";
        return null;
    }

    static int CheckRanges(FeedSpawn f, ResourceNode node, List<string> msgs)
    {
        if (node.Ranges == null || node.Ranges.Count == 0) return 0;
        int n = 0;
        foreach (var (stat, val) in f.Stats)
        {
            if (!node.Ranges.TryGetValue(stat, out var r))
            { n++; msgs.Add($"{f.Name}: {stat} is not in the ranges defined for {node.Name}."); }
            else if (val < r.Min || val > r.Max)
            { n++; msgs.Add($"{f.Name}: {stat}={val} is outside {node.Name} range {r.Min}-{r.Max}."); }
        }
        return n;
    }

    static bool SameStats(Dictionary<Stat, int> a, IReadOnlyDictionary<Stat, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    Existing? Find(SqliteConnection c, SqliteTransaction tx, string name, string where, params (string, object?)[] extra)
    {
        var ps = new List<(string, object?)> { ("@g", GalaxyId), ("@n", name) };
        ps.AddRange(extra);
        using var cmd = Sql.Cmd(c, tx,
            $"SELECT id, resource_type_id, spawned_utc, {StatColList} FROM spawn " +
            $"WHERE galaxy_id = @g AND name = @n AND {where} ORDER BY id DESC LIMIT 1", ps.ToArray());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var stats = new Dictionary<Stat, int>();
        for (int i = 0; i < StatCols.Length; i++)
            if (!r.IsDBNull(3 + i)) stats[StatInfo.All[i]] = r.GetInt32(3 + i);
        return new Existing(r.GetInt64(0), r.GetString(1), r.GetString(2), stats);
    }

    static List<(string, object?)> StatParams(IReadOnlyDictionary<Stat, int> stats)
    {
        var ps = new List<(string, object?)>();
        for (int i = 0; i < StatCols.Length; i++)
            ps.Add(("@" + StatCols[i], stats.TryGetValue(StatInfo.All[i], out var v) ? (object?)v : null));
        return ps;
    }

    long Insert(SqliteConnection c, SqliteTransaction tx, FeedSpawn f, string typeId, string spawnedText, string nowText)
    {
        var ps = StatParams(f.Stats);
        ps.Add(("@g", GalaxyId)); ps.Add(("@n", f.Name)); ps.Add(("@t", typeId));
        ps.Add(("@sp", spawnedText)); ps.Add(("@now", nowText));
        Sql.Exec(c, tx,
            $"INSERT INTO spawn (galaxy_id, name, resource_type_id, {StatColList}, spawned_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES (@g, @n, @t, {StatParamList}, @sp, @now, @now)", ps.ToArray());
        return Convert.ToInt64(Sql.Scalar(c, tx, "SELECT last_insert_rowid()"));
    }

    static void UpdateStats(SqliteConnection c, SqliteTransaction tx, long id, IReadOnlyDictionary<Stat, int> stats, string nowText)
    {
        var ps = StatParams(stats);
        ps.Add(("@id", id)); ps.Add(("@now", nowText));
        Sql.Exec(c, tx, $"UPDATE spawn SET {StatSetList}, last_seen_utc = @now WHERE id = @id", ps.ToArray());
    }

    static bool SyncPlanets(SqliteConnection c, SqliteTransaction tx, long spawnId, IReadOnlyList<string> incoming, string nowText)
    {
        bool changed = false;
        var active = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = Sql.Cmd(c, tx, "SELECT planet FROM spawn_planet WHERE spawn_id = @id AND removed_utc IS NULL", ("@id", spawnId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) active.Add(r.GetString(0));

        foreach (var p in incoming)
        {
            if (active.Remove(p)) continue;
            Sql.Exec(c, tx,
                "INSERT INTO spawn_planet (spawn_id, planet, first_seen_utc) VALUES (@id, @p, @now) " +
                "ON CONFLICT(spawn_id, planet) DO UPDATE SET removed_utc = NULL",
                ("@id", spawnId), ("@p", p), ("@now", nowText));
            changed = true;
        }
        foreach (var gone in active)
        {
            Sql.Exec(c, tx, "UPDATE spawn_planet SET removed_utc = @now WHERE spawn_id = @id AND planet = @p",
                ("@now", nowText), ("@id", spawnId), ("@p", gone));
            changed = true;
        }
        return changed;
    }

    void WriteLog(DateTimeOffset now, string status, SyncResult? r, string message)
    {
        try
        {
            using var c = _db.Open();
            var t = DbTime.ToDb(now);
            Sql.Exec(c, null,
                "INSERT INTO sync_log (source, galaxy_id, started_utc, finished_utc, status, spawns_added, spawns_updated, " +
                "spawns_reopened, spawns_replaced, spawns_despawned, unmapped_count, rejected_count, message) " +
                "VALUES ('galaxyHarvester', @g, @t, @t, @st, @a, @u, @ro, @rp, @d, @un, @rj, @m)",
                ("@g", GalaxyId), ("@t", t), ("@st", status),
                ("@a", r?.Added ?? 0), ("@u", r?.Updated ?? 0), ("@ro", r?.Reopened ?? 0), ("@rp", r?.Replaced ?? 0),
                ("@d", r?.Despawned ?? 0), ("@un", r?.Unmapped ?? 0), ("@rj", r?.Rejected ?? 0), ("@m", message));
        }
        catch (SqliteException) { /* logging must never mask the real outcome */ }
    }
}
