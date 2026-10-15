using SwgCraft.Core;

namespace SwgCraft.Desktop;

public sealed class TabVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _name = "";

    public TabVm(ChangeTracker t) => _t = t;

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

    public static TabVm CreateNew(ChangeTracker t)
    {
        var vm = new TabVm(t) { IdFollowsName = true };
        vm.Name = "New Tab";
        return vm;
    }

    public static TabVm FromModel(CraftTab m, ChangeTracker t) => new(t) { _id = m.Id, _name = m.Name };

    public CraftTab ToModel() => new() { Id = Id.Trim(), Name = Name.Trim() };
}
