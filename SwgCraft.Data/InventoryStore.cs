using Microsoft.Data.Sqlite;
using SwgCraft.Core;

namespace SwgCraft.Data;

/// <summary>One stack of a harvested resource spawn in the active galaxy's inventory.</summary>
public sealed record InventoryItem(long Id, Spawn Spawn, int Quantity, string? Note, DateTimeOffset UpdatedUtc,
                                   double? PricePerUnit = null);

// Second half of GalaxySpawnStore (the other half in SpawnStore.cs must also say "partial").
internal sealed partial class GalaxySpawnStore
{
    // A property, not a field: static field initializers across partial files run in an undefined order.
    // New columns go at the end so existing offsets stay valid.
    static string SpawnColumns =>
        "s.id, s.galaxy_id, s.name, s.resource_type_id, " +
        string.Join(", ", StatCols.Select(x => "s." + x)) +
        ", s.spawned_utc, s.first_seen_utc, s.last_seen_utc, s.despawned_utc, " +
        "(SELECT group_concat(p.planet, ', ') FROM spawn_planet p WHERE p.spawn_id = s.id AND p.removed_utc IS NULL), " +
        "s.source, s.filtered, s.generic";

    /// <summary>Reads a spawn whose columns start at index <paramref name="s"/> (layout of <see cref="SpawnColumns"/>).</summary>
    static Spawn ReadSpawn(SqliteDataReader r, int s)
    {
        int n = StatCols.Length;
        var stats = new Dictionary<Stat, int>();
        for (int i = 0; i < n; i++)
            if (!r.IsDBNull(s + 4 + i)) stats[StatInfo.All[i]] = r.GetInt32(s + 4 + i);
        int t = s + 4 + n;
        return new Spawn(
            r.GetInt64(s), r.GetInt32(s + 1), r.GetString(s + 2), r.GetString(s + 3), stats,
            DbTime.FromDb(r.GetString(t)), DbTime.FromDb(r.GetString(t + 1)), DbTime.FromDb(r.GetString(t + 2)),
            r.IsDBNull(t + 3) ? null : DbTime.FromDb(r.GetString(t + 3)),
            r.IsDBNull(t + 4) ? "" : r.GetString(t + 4),
            r.GetString(t + 5), r.GetInt32(t + 6) != 0, r.GetInt32(t + 7) != 0);
    }

    /// <summary>All stacks for this galaxy, including resources that have since despawned.</summary>
    public IReadOnlyList<InventoryItem> Inventory()
    {
        using var c = _db.Open();
        using var cmd = Sql.Cmd(c, null,
            $"SELECT i.id, i.quantity, i.note, i.updated_utc, i.price_per_unit, {SpawnColumns} " +
            "FROM inventory i JOIN spawn s ON s.id = i.spawn_id AND s.galaxy_id = i.galaxy_id " +
            "WHERE i.galaxy_id = @g ORDER BY s.name", ("@g", GalaxyId));
        using var r = cmd.ExecuteReader();
        var list = new List<InventoryItem>();
        while (r.Read())
            list.Add(new InventoryItem(r.GetInt64(0), ReadSpawn(r, 5), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetString(2), DbTime.FromDb(r.GetString(3)),
                r.IsDBNull(4) ? null : r.GetDouble(4)));
        return list;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> (negative to use up) to a spawn's stack. Returns the new quantity, or null if it
    /// would go below zero (nothing is changed). Reaching zero removes the stack. A non-empty note replaces the old one.
    /// The price per unit is never touched here.
    /// </summary>
    public int? AdjustInventory(long spawnId, int delta, string? note = null)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var cur = Sql.Scalar(c, tx, "SELECT quantity FROM inventory WHERE galaxy_id = @g AND spawn_id = @s",
            ("@g", GalaxyId), ("@s", spawnId));
        long next = (cur == null ? 0 : Convert.ToInt64(cur)) + delta;
        if (next < 0 || next > int.MaxValue) return null;

        if (next == 0) Delete(c, tx, spawnId);
        else Upsert(c, tx, spawnId, (int)next, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), preserveNote: true);
        tx.Commit();
        return (int)next;
    }

    /// <summary>Sets the stack to exactly <paramref name="quantity"/> (0 or less removes it) and writes the note as given.</summary>
    public void SetInventory(long spawnId, int quantity, string? note)
    {
        using var c = _db.Open();
        if (quantity <= 0) Delete(c, null, spawnId);
        else Upsert(c, null, spawnId, quantity, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), preserveNote: false);
    }

    /// <summary>Sets or clears (null) the price per unit of an existing stack. Does nothing if there is no stack.</summary>
    public void SetPrice(long spawnId, double? pricePerUnit)
    {
        using var c = _db.Open();
        Sql.Exec(c, null,
            "UPDATE inventory SET price_per_unit = @p, updated_utc = @t WHERE galaxy_id = @g AND spawn_id = @s",
            ("@p", pricePerUnit), ("@t", DbTime.ToDb(DateTimeOffset.UtcNow)), ("@g", GalaxyId), ("@s", spawnId));
    }

    /// <summary>Filter flag: the spawn is skipped by build generation.</summary>
    public void SetFiltered(long spawnId, bool value) => SetFlag("filtered", spawnId, value);

    /// <summary>Generic flag: the spawn may be used where stats and experimentation do not matter.</summary>
    public void SetGeneric(long spawnId, bool value) => SetFlag("generic", spawnId, value);

    void SetFlag(string column, long spawnId, bool value)
    {
        using var c = _db.Open();   // column is one of two internal constants, never user input
        Sql.Exec(c, null, $"UPDATE spawn SET {column} = @v WHERE id = @id AND galaxy_id = @g",
            ("@v", value ? 1 : 0), ("@id", spawnId), ("@g", GalaxyId));
    }

    void Delete(SqliteConnection c, SqliteTransaction? tx, long spawnId) =>
        Sql.Exec(c, tx, "DELETE FROM inventory WHERE galaxy_id = @g AND spawn_id = @s", ("@g", GalaxyId), ("@s", spawnId));

    void Upsert(SqliteConnection c, SqliteTransaction? tx, long spawnId, int quantity, string? note, bool preserveNote)
    {
        var noteExpr = preserveNote ? "COALESCE(excluded.note, note)" : "excluded.note";
        // The composite foreign key (galaxy_id, spawn_id) rejects spawns that belong to another galaxy.
        // price_per_unit is deliberately absent: new stacks start blank and updates leave it alone.
        Sql.Exec(c, tx,
            "INSERT INTO inventory (galaxy_id, spawn_id, quantity, note, updated_utc) VALUES (@g, @s, @q, @n, @t) " +
            $"ON CONFLICT(galaxy_id, spawn_id) DO UPDATE SET quantity = excluded.quantity, note = {noteExpr}, " +
            "updated_utc = excluded.updated_utc",
            ("@g", GalaxyId), ("@s", spawnId), ("@q", quantity), ("@n", note), ("@t", DbTime.ToDb(DateTimeOffset.UtcNow)));
    }
}
