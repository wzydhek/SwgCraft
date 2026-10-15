using SwgCraft.Core;

namespace SwgCraft.Desktop;

public sealed class LootItemVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _name = "";

    public LootItemVm(ChangeTracker t) => _t = t;

    /// <summary>New items derive their ID from the name until the first save or a manual ID edit. Saved IDs never change by themselves.</summary>
    public bool IdFollowsName { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            if (IdFollowsName) Set(ref _id, Slug.From(value), nameof(Id));
        }
    }

    public string Id
    {
        get => _id;
        set { if (Set(ref _id, value)) IdFollowsName = false; }
    }

    protected override void OnChanged() => _t.Touch();

    public static LootItemVm CreateNew(ChangeTracker t)
    {
        var vm = new LootItemVm(t) { IdFollowsName = true };
        vm.Name = "New Looted Item";
        return vm;
    }

    public static LootItemVm FromModel(LootItem m, ChangeTracker t) => new(t) { _id = m.Id, _name = m.Name };

    public LootItem ToModel() => new() { Id = Id.Trim(), Name = Name.Trim() };
}
