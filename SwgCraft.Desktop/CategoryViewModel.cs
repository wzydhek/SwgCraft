using SwgCraft.Core;

namespace SwgCraft.Desktop;

public sealed class CategoryVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _name = "";

    public CategoryVm(ChangeTracker t) => _t = t;

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

    public static CategoryVm CreateNew(ChangeTracker t)
    {
        var vm = new CategoryVm(t) { IdFollowsName = true };
        vm.Name = "New Category";
        return vm;
    }

    public static CategoryVm FromModel(CraftCategory m, ChangeTracker t) => new(t) { _id = m.Id, _name = m.Name };

    public CraftCategory ToModel() => new() { Id = Id.Trim(), Name = Name.Trim() };
}
