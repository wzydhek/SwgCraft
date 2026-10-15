using Microsoft.Data.Sqlite;
using SwgCraft.Core;

namespace SwgCraft.Data;

/// <summary>
/// One stack of a looted or crafted item in the active galaxy's inventory. <see cref="ItemId"/> is a looted item ID
/// (<see cref="MemberKind.LootItem"/>) or a schematic ID whose product it is (<see cref="MemberKind.Schematic"/>).
/// Units with the same serial number come from one factory run, which is what an "identical" component slot needs.
/// </summary>
public sealed record ItemStack(long Id, MemberKind Kind, string ItemId, string? Serial, int Quantity, string? Note, DateTimeOffset UpdatedUtc);

public enum ItemStackUpdate { Updated, Removed, NotFound, SerialTaken }

// Third part of GalaxySpawnStore (see SpawnStore.cs and InventoryStore.cs).
internal sealed partial class GalaxySpawnStore
{
    static string KindText(MemberKind kind) => kind == MemberKind.LootItem ? "LootItem" : "Schematic";
    static MemberKind KindFrom(string text) => text == "LootItem" ? MemberKind.LootItem : MemberKind.Schematic;

    /// <summary>"" for no serial, so one stack per item + serial is enforced by the table.</summary>
    static string SerialToDb(string? serial) => (serial ?? "").Trim();

    /// <summary>All item stacks in this galaxy.</summary>
    public IReadOnlyList<ItemStack> ItemInventory()
    {
        using var c = _db.Open();
        using var cmd = Sql.Cmd(c, null,
            "SELECT id, item_kind, item_id, serial, quantity, note, updated_utc FROM item_inventory " +
            "WHERE galaxy_id = @g ORDER BY item_kind, item_id, serial", ("@g", GalaxyId));
        using var r = cmd.ExecuteReader();
        var list = new List<ItemStack>();
        while (r.Read())
        {
            var serial = r.GetString(3);
            list.Add(new ItemStack(r.GetInt64(0), KindFrom(r.GetString(1)), r.GetString(2), serial.Length == 0 ? null : serial,
                r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), DbTime.FromDb(r.GetString(6))));
        }
        return list;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> (negative to use up) to the stack of this item and serial, creating it if needed.
    /// Returns the new quantity, or null if it would go below zero (nothing is changed). Reaching zero removes the stack.
    /// A non-empty note replaces the old one.
    /// </summary>
    public int? AdjustItem(MemberKind kind, string itemId, string? serial, int delta, string? note = null)
    {
        itemId = (itemId ?? "").Trim();
        if (itemId.Length == 0) throw new ArgumentException("An item is required.", nameof(itemId));
        var s = SerialToDb(serial);
        var n = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var cur = Sql.Scalar(c, tx,
            "SELECT quantity FROM item_inventory WHERE galaxy_id = @g AND item_kind = @k AND item_id = @i AND serial = @s",
            ("@g", GalaxyId), ("@k", KindText(kind)), ("@i", itemId), ("@s", s));
        long next = (cur == null ? 0 : Convert.ToInt64(cur)) + delta;
        if (next < 0 || next > int.MaxValue) return null;

        if (next == 0)
            Sql.Exec(c, tx, "DELETE FROM item_inventory WHERE galaxy_id = @g AND item_kind = @k AND item_id = @i AND serial = @s",
                ("@g", GalaxyId), ("@k", KindText(kind)), ("@i", itemId), ("@s", s));
        else
            Sql.Exec(c, tx,
                "INSERT INTO item_inventory (galaxy_id, item_kind, item_id, serial, quantity, note, updated_utc) " +
                "VALUES (@g, @k, @i, @s, @q, @n, @t) " +
                "ON CONFLICT(galaxy_id, item_kind, item_id, serial) DO UPDATE SET quantity = excluded.quantity, " +
                "note = COALESCE(excluded.note, note), updated_utc = excluded.updated_utc",
                ("@g", GalaxyId), ("@k", KindText(kind)), ("@i", itemId), ("@s", s), ("@q", (int)next), ("@n", n),
                ("@t", DbTime.ToDb(DateTimeOffset.UtcNow)));
        tx.Commit();
        return (int)next;
    }

    /// <summary>
    /// Edits a stack: its serial, quantity (0 or less removes it) and note (written as given).
    /// Refuses (<see cref="ItemStackUpdate.SerialTaken"/>) a serial that another stack of the same item already has.
    /// </summary>
    public ItemStackUpdate UpdateItemStack(long stackId, string? serial, int quantity, string? note)
    {
        using var c = _db.Open();
        if (quantity <= 0)
        {
            int gone = Sql.Exec(c, null, "DELETE FROM item_inventory WHERE id = @id AND galaxy_id = @g", ("@id", stackId), ("@g", GalaxyId));
            return gone == 0 ? ItemStackUpdate.NotFound : ItemStackUpdate.Removed;
        }
        try
        {
            int n = Sql.Exec(c, null,
                "UPDATE item_inventory SET serial = @s, quantity = @q, note = @n, updated_utc = @t WHERE id = @id AND galaxy_id = @g",
                ("@s", SerialToDb(serial)), ("@q", quantity), ("@n", string.IsNullOrWhiteSpace(note) ? null : note.Trim()),
                ("@t", DbTime.ToDb(DateTimeOffset.UtcNow)), ("@id", stackId), ("@g", GalaxyId));
            return n == 0 ? ItemStackUpdate.NotFound : ItemStackUpdate.Updated;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT: the unique item + serial
        {
            return ItemStackUpdate.SerialTaken;
        }
    }

    /// <summary>Removes a stack. Returns false if there was none.</summary>
    public bool DeleteItemStack(long stackId)
    {
        using var c = _db.Open();
        return Sql.Exec(c, null, "DELETE FROM item_inventory WHERE id = @id AND galaxy_id = @g", ("@id", stackId), ("@g", GalaxyId)) > 0;
    }
}
