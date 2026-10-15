using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public sealed class ItemInventoryTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.db");
    readonly SpawnStore _store;
    readonly IGalaxySpawnStore _g;

    public ItemInventoryTests()
    {
        var db = new SpawnDatabase(_file);
        db.Migrate();
        _store = new SpawnStore(db);
        _store.UpsertGalaxy(118, "Finalizer");
        _store.UpsertGalaxy(119, "Other");
        _g = _store.ForGalaxy(118);
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + "*"))
            try { File.Delete(f); } catch (IOException) { }
    }

    [Fact]
    public void Adding_the_same_item_and_serial_grows_one_stack_and_other_serials_get_their_own()
    {
        Assert.Equal(2, _g.AdjustItem(MemberKind.Schematic, "blaster_power_handler", "123456", 2));
        Assert.Equal(5, _g.AdjustItem(MemberKind.Schematic, "blaster_power_handler", " 123456 ", 3));   // serial is trimmed
        Assert.Equal(1, _g.AdjustItem(MemberKind.Schematic, "blaster_power_handler", "999", 1));
        Assert.Equal(4, _g.AdjustItem(MemberKind.Schematic, "blaster_power_handler", null, 4));
        Assert.Equal(4, _g.AdjustItem(MemberKind.Schematic, "blaster_power_handler", "", 0));   // "" is the same as no serial
        Assert.Equal(6, _g.AdjustItem(MemberKind.LootItem, "precision_scope", "", 6));

        var stacks = _g.ItemInventory();
        Assert.Equal(4, stacks.Count);
        Assert.Equal(5, stacks.Single(s => s.Serial == "123456").Quantity);
        Assert.Equal(4, stacks.Single(s => s.Kind == MemberKind.Schematic && s.Serial == null).Quantity);
        Assert.Equal(MemberKind.LootItem, stacks.Single(s => s.ItemId == "precision_scope").Kind);
    }

    [Fact]
    public void Using_more_than_a_stack_has_changes_nothing_and_using_it_all_removes_it()
    {
        _g.AdjustItem(MemberKind.LootItem, "precision_scope", null, 3);
        Assert.Null(_g.AdjustItem(MemberKind.LootItem, "precision_scope", null, -4));
        Assert.Equal(3, _g.ItemInventory().Single().Quantity);
        Assert.Equal(0, _g.AdjustItem(MemberKind.LootItem, "precision_scope", null, -3));
        Assert.Empty(_g.ItemInventory());
    }

    [Fact]
    public void A_stack_can_be_edited_but_not_given_a_serial_another_stack_of_that_item_has()
    {
        _g.AdjustItem(MemberKind.Schematic, "stock", "A1", 2);
        _g.AdjustItem(MemberKind.Schematic, "stock", "B2", 3, "spare");
        _g.AdjustItem(MemberKind.Schematic, "barrel", "B2", 1);
        var a1 = _g.ItemInventory().Single(s => s.ItemId == "stock" && s.Serial == "A1");

        Assert.Equal(ItemStackUpdate.SerialTaken, _g.UpdateItemStack(a1.Id, "B2", 2, null));
        Assert.Equal(ItemStackUpdate.Updated, _g.UpdateItemStack(a1.Id, "C3", 7, "kept for rifles"));   // B2 on a different item doesn't matter
        var edited = _g.ItemInventory().Single(s => s.Id == a1.Id);
        Assert.Equal(("C3", 7, "kept for rifles"), (edited.Serial, edited.Quantity, edited.Note));

        Assert.Equal(ItemStackUpdate.Updated, _g.UpdateItemStack(a1.Id, "", 7, null));   // clearing the serial
        Assert.Null(_g.ItemInventory().Single(s => s.Id == a1.Id).Serial);
        Assert.Equal(ItemStackUpdate.Removed, _g.UpdateItemStack(a1.Id, null, 0, null));
        Assert.Equal(ItemStackUpdate.NotFound, _g.UpdateItemStack(a1.Id, null, 1, null));
    }

    [Fact]
    public void A_note_is_kept_when_adding_without_one()
    {
        _g.AdjustItem(MemberKind.LootItem, "precision_scope", null, 1, "from the Krayt");
        _g.AdjustItem(MemberKind.LootItem, "precision_scope", null, 1);
        Assert.Equal("from the Krayt", _g.ItemInventory().Single().Note);
    }

    [Fact]
    public void Galaxies_never_see_each_others_items()
    {
        _g.AdjustItem(MemberKind.LootItem, "precision_scope", null, 2);
        var other = _store.ForGalaxy(119);
        Assert.Empty(other.ItemInventory());
        var id = _g.ItemInventory().Single().Id;
        Assert.False(other.DeleteItemStack(id));
        Assert.Equal(ItemStackUpdate.NotFound, other.UpdateItemStack(id, null, 9, null));
        Assert.True(_g.DeleteItemStack(id));
    }
}
