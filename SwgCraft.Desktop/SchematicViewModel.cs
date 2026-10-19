using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

public sealed class ProfessionPickVm : ObservableObject
{
    readonly ChangeTracker _t;
    bool _isSelected;

    public ProfessionPickVm(string id, string name, bool selected, ChangeTracker t)
    {
        Id = id; Name = name; _isSelected = selected; _t = t;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    protected override void OnChanged() => _t.Touch();
}

public sealed class ComponentSlotVm : ObservableObject
{
    public const string SchematicPrefix = "schematic:";
    public const string ClassPrefix = "class:";
    public const string LootPrefix = "loot:";

    readonly ChangeTracker _t;
    string _key = "", _name = "";
    int _quantity = 1;
    bool _identical, _optional;

    public ComponentSlotVm(ChangeTracker t) => _t = t;

    public static string KeyFor(SlotKind kind, string id) => (kind switch
    {
        SlotKind.Class => ClassPrefix,
        SlotKind.LootItem => LootPrefix,
        _ => SchematicPrefix,
    }) + id;

    /// <summary>"schematic:id", "class:id" or "loot:id". A null write (a picker losing its list) is ignored so a refresh can't wipe the choice.</summary>
    public string Key { get => _key; set { if (value != null) Set(ref _key, value); } }
    public void RaiseKey() => Raise(nameof(Key));

    /// <summary>The slot's label in the game's crafting window.</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public int Quantity { get => _quantity; set => Set(ref _quantity, value); }
    public bool Identical { get => _identical; set => Set(ref _identical, value); }
    public bool Optional { get => _optional; set => Set(ref _optional, value); }

    protected override void OnChanged() => _t.Touch();

    public static ComponentSlotVm FromModel(ComponentSlot m, ChangeTracker t) => new(t)
    {
        _key = KeyFor(m.Kind, m.Target), _name = m.Name ?? "", _quantity = m.Quantity, _identical = m.Identical, _optional = m.Optional
    };

    public ComponentSlot ToModel()
    {
        var (kind, id) =
            _key.StartsWith(ClassPrefix, StringComparison.Ordinal) ? (SlotKind.Class, _key[ClassPrefix.Length..]) :
            _key.StartsWith(LootPrefix, StringComparison.Ordinal) ? (SlotKind.LootItem, _key[LootPrefix.Length..]) :
            _key.StartsWith(SchematicPrefix, StringComparison.Ordinal) ? (SlotKind.Schematic, _key[SchematicPrefix.Length..]) :
            (SlotKind.Schematic, "");
        return new ComponentSlot
        {
            Name = string.IsNullOrWhiteSpace(_name) ? null : _name.Trim(),
            Kind = kind,
            Target = id, Quantity = _quantity, Identical = _identical, Optional = _optional
        };
    }
}

/// <summary>One skill that can craft the schematic.</summary>
public sealed class SkillSlotVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _skill = "";

    public SkillSlotVm(ChangeTracker t) => _t = t;

    /// <summary>Skill ID. A null write (a picker losing its list) is ignored so a refresh can't wipe the choice.</summary>
    public string Skill { get => _skill; set { if (value != null) Set(ref _skill, value); } }
    public void RaiseSkill() => Raise(nameof(Skill));

    protected override void OnChanged() => _t.Touch();

    public static SkillSlotVm FromModel(string skillId, ChangeTracker t) => new(t) { _skill = skillId };
}

public sealed class ResourceSlotVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _resource = "", _name = "";
    int _amount = 1;

    public ResourceSlotVm(ChangeTracker t) => _t = t;

    /// <summary>The slot's label in the game's crafting window.</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    /// <summary>Resource-tree node ID. A null write (a picker losing its list) is ignored so a refresh can't wipe the choice.</summary>
    public string Resource { get => _resource; set { if (value != null) Set(ref _resource, value); } }
    public void RaiseResource() => Raise(nameof(Resource));

    public int Amount { get => _amount; set => Set(ref _amount, value); }

    protected override void OnChanged() => _t.Touch();

    public static ResourceSlotVm FromModel(ResourceSlot m, ChangeTracker t) => new(t) { _name = m.Name ?? "", _resource = m.Resource, _amount = m.Amount };
    public ResourceSlot ToModel() => new() { Name = string.IsNullOrWhiteSpace(_name) ? null : _name.Trim(), Resource = _resource, Amount = _amount };
}

/// <summary>
/// One modifier of an experimental (Charges, Effective Range, ...). The eleven stat properties are what the grid binds to;
/// a blank cell is 0 and is not saved.
/// </summary>
public sealed class ModifierVm : ObservableObject
{
    readonly ChangeTracker _t;
    readonly int[] _w = new int[StatInfo.All.Count];
    string _name = "";

    public ModifierVm(ChangeTracker t) => _t = t;

    public string Name { get => _name; set => Set(ref _name, value); }

    int? Get(Stat s) { var v = _w[(int)s]; return v == 0 ? null : v; }

    void Put(Stat s, int? v, string property)
    {
        int nv = v ?? 0;
        if (_w[(int)s] == nv) return;
        _w[(int)s] = nv;
        Raise(property);
        Raise(nameof(Total));
        Raise(nameof(TotalOk));
        OnChanged();
    }

    public int? ER { get => Get(Stat.ER); set => Put(Stat.ER, value, nameof(ER)); }
    public int? CR { get => Get(Stat.CR); set => Put(Stat.CR, value, nameof(CR)); }
    public int? CD { get => Get(Stat.CD); set => Put(Stat.CD, value, nameof(CD)); }
    public int? DR { get => Get(Stat.DR); set => Put(Stat.DR, value, nameof(DR)); }
    public int? FL { get => Get(Stat.FL); set => Put(Stat.FL, value, nameof(FL)); }
    public int? HR { get => Get(Stat.HR); set => Put(Stat.HR, value, nameof(HR)); }
    public int? MB { get => Get(Stat.MB); set => Put(Stat.MB, value, nameof(MB)); }
    public int? PE { get => Get(Stat.PE); set => Put(Stat.PE, value, nameof(PE)); }
    public int? OQ { get => Get(Stat.OQ); set => Put(Stat.OQ, value, nameof(OQ)); }
    public int? SR { get => Get(Stat.SR); set => Put(Stat.SR, value, nameof(SR)); }
    public int? UT { get => Get(Stat.UT); set => Put(Stat.UT, value, nameof(UT)); }

    public int Total => _w.Sum();
    public bool TotalOk => Total == 100;

    protected override void OnChanged() => _t.Touch();

    public static ModifierVm FromModel(ExperimentModifier m, ChangeTracker t)
    {
        var vm = new ModifierVm(t) { _name = m.Name };
        foreach (var (stat, w) in m.Weights) vm._w[(int)stat] = w;
        return vm;
    }

    public ExperimentModifier ToModel()
    {
        var e = new ExperimentModifier { Name = _name.Trim() };
        foreach (var stat in StatInfo.All)
            if (_w[(int)stat] != 0) e.Weights[stat] = _w[(int)stat];
        return e;
    }
}

/// <summary>
/// One experimental (what experimentation points are spent on) and the modifiers it raises.
/// While it has exactly one modifier whose name matches its own (without the "Experimental " prefix), renaming the experimental
/// renames that modifier too: typing "Experimental Charges" names the modifier "Charges".
/// </summary>
public sealed class ExperimentVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _name = "";
    SortedDictionary<Stat, int>? _legacyWeights;   // only if the file had both old percentages and modifiers; kept so nothing is lost

    public ExperimentVm(ChangeTracker t)
    {
        _t = t;
        Modifiers.CollectionChanged += ModifiersChanged;
    }

    public const string NamePrefix = "Experimental ";

    /// <summary>A new experimental named "Experimental " (ready for the rest of the name) with one modifier that follows its name.</summary>
    public static ExperimentVm CreateNew(ChangeTracker t)
    {
        var vm = new ExperimentVm(t);
        vm.Modifiers.Add(new ModifierVm(t));
        vm.Name = NamePrefix;
        return vm;
    }

    /// <summary>"Experimental Charges" -> "Charges"; a name without the prefix is used as it is.</summary>
    public static string ModifierNameFor(string? experimentalName)
    {
        var n = (experimentalName ?? "").Trim();
        var prefix = NamePrefix.Trim();
        if (n.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return "";
        return n.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase) ? n[NamePrefix.Length..].Trim() : n;
    }

    public ObservableCollection<ModifierVm> Modifiers { get; } = new();

    public string Name
    {
        get => _name;
        set
        {
            var old = _name;
            if (!Set(ref _name, value)) return;
            if (Modifiers.Count == 1 && (Modifiers[0].Name ?? "").Trim() == ModifierNameFor(old)) Modifiers[0].Name = ModifierNameFor(value);
        }
    }

    /// <summary>The modifier names, for the experimentals grid.</summary>
    public string Summary => Modifiers.Count == 0 ? "(no modifiers)" : string.Join(", ", Modifiers.Select(m => string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name.Trim()));

    /// <summary>False if there are no modifiers or any modifier's percentages don't total 100.</summary>
    public bool AllOk => Modifiers.Count > 0 && Modifiers.All(m => m.TotalOk);

    void ModifiersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (ModifierVm m in e.OldItems) m.PropertyChanged -= ModifierPropertyChanged;
        if (e.NewItems != null) foreach (ModifierVm m in e.NewItems) m.PropertyChanged += ModifierPropertyChanged;
        RaiseSummary();
        _t.Touch();
    }

    void ModifierPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModifierVm.Name) or nameof(ModifierVm.TotalOk)) RaiseSummary();
    }

    void RaiseSummary()
    {
        Raise(nameof(Summary));
        Raise(nameof(AllOk));
    }

    protected override void OnChanged() => _t.Touch();

    public static ExperimentVm FromModel(Experiment m, ChangeTracker t)
    {
        var vm = new ExperimentVm(t) { _name = m.Name, _legacyWeights = m.LegacyWeights };
        foreach (var mod in m.Modifiers) vm.Modifiers.Add(ModifierVm.FromModel(mod, t));
        return vm;
    }

    public Experiment ToModel()
    {
        var e = new Experiment { Name = _name.Trim(), LegacyWeights = _legacyWeights };
        foreach (var mod in Modifiers) e.Modifiers.Add(mod.ToModel());
        return e;
    }
}

public sealed class SchematicVm : ObservableObject
{
    readonly ChangeTracker _t;
    string _id = "", _name = "", _category = "", _tab = "";
    int? _complexity, _size;
    int _showIndex;                       // 0 = default, 1 = yes, 2 = no
    bool _ignoreExperimentation;
    List<string> _extraProfessions = new();   // IDs not in the current profession list; kept so nothing is lost silently
    Dictionary<string, string> _skillOwner = new(StringComparer.Ordinal);   // skill ID -> profession ID

    public SchematicVm(ChangeTracker t)
    {
        _t = t;
        Components.CollectionChanged += Touch;
        Resources.CollectionChanged += Touch;
        Experiments.CollectionChanged += Touch;
        Skills.CollectionChanged += SkillsChanged;
    }

    void SkillsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (SkillSlotVm s in e.OldItems) s.PropertyChanged -= SkillPicked;
        if (e.NewItems != null) foreach (SkillSlotVm s in e.NewItems) s.PropertyChanged += SkillPicked;
        _t.Touch();
    }

    /// <summary>Picking a skill ticks the profession it belongs to, so the two lists agree without extra clicks.</summary>
    void SkillPicked(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SkillSlotVm.Skill) || sender is not SkillSlotVm s) return;
        if (!_skillOwner.TryGetValue(s.Skill ?? "", out var profId)) return;
        var pick = Professions.FirstOrDefault(p => p.Id == profId);
        if (pick != null) pick.IsSelected = true;
    }

    void Touch(object? sender, NotifyCollectionChangedEventArgs e) => _t.Touch();

    public ObservableCollection<ProfessionPickVm> Professions { get; } = new();
    public ObservableCollection<ComponentSlotVm> Components { get; } = new();
    public ObservableCollection<ResourceSlotVm> Resources { get; } = new();
    public ObservableCollection<ExperimentVm> Experiments { get; } = new();
    public ObservableCollection<SkillSlotVm> Skills { get; } = new();

    /// <summary>New schematics derive their ID from the name until the first save or a manual ID edit. Saved IDs never change by themselves.</summary>
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

    public string Category { get => _category; set => Set(ref _category, value); }

    /// <summary>ID of the SWGEmu crafting tab (tabs.json); empty for none.</summary>
    public string Tab { get => _tab; set => Set(ref _tab, value ?? ""); }
    public int? Complexity { get => _complexity; set => Set(ref _complexity, value); }
    public int? Size { get => _size; set => Set(ref _size, value); }
    public int ShowIndex { get => _showIndex; set => Set(ref _showIndex, value); }

    /// <summary>Builds skip this schematic's experimentation (the game never uses these values).</summary>
    public bool IgnoreExperimentation { get => _ignoreExperimentation; set => Set(ref _ignoreExperimentation, value); }

    protected override void OnChanged() => _t.Touch();

    // ------------------------------------------------------------ category

    /// <summary>
    /// Older data may hold a category's name (or a different capitalisation of its ID) instead of its ID. Switches such a value to the real ID
    /// without marking the schematic as edited; a value that matches nothing is left alone so validation can point it out.
    /// </summary>
    public void ResolveCategory(IReadOnlyList<CraftCategory> categories)
    {
        var c = (_category ?? "").Trim();
        if (c.Length == 0 || categories.Any(x => x.Id == c)) return;
        var match = categories.FirstOrDefault(x => string.Equals(x.Name?.Trim(), c, StringComparison.OrdinalIgnoreCase))
                    ?? categories.FirstOrDefault(x => string.Equals(x.Id, c, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;
        _category = match.Id;
        Raise(nameof(Category));
    }

    // ------------------------------------------------------------ professions

    public IEnumerable<string> SelectedProfessionIds() =>
        Professions.Where(p => p.IsSelected).Select(p => p.Id).Concat(_extraProfessions);

    /// <summary>Rebuilds the profession check boxes from the catalog, keeping what is ticked (IDs not in the catalog are kept aside).</summary>
    public void SetProfessionCatalog(IReadOnlyList<Profession> catalog)
    {
        var wanted = SelectedProfessionIds().ToList();
        var chosen = new HashSet<string>(wanted, StringComparer.Ordinal);
        Professions.Clear();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in catalog)
        {
            if (string.IsNullOrEmpty(p.Id) || !known.Add(p.Id)) continue;
            Professions.Add(new ProfessionPickVm(p.Id, p.Name, chosen.Contains(p.Id), _t));
        }
        _extraProfessions = wanted.Where(id => !known.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        _skillOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in catalog)
            foreach (var sk in p.Skills)
                if (!string.IsNullOrEmpty(sk.Id) && !string.IsNullOrEmpty(p.Id)) _skillOwner.TryAdd(sk.Id, p.Id);
        Raise(nameof(UnknownProfessions));
    }

    /// <summary>Profession IDs this schematic lists that no longer exist, shown so the user can see why validation complains.</summary>
    public string UnknownProfessions => _extraProfessions.Count == 0 ? "" : "Unknown professions: " + string.Join(", ", _extraProfessions);

    // ------------------------------------------------------------ conversion

    public static SchematicVm CreateNew(ChangeTracker t, IReadOnlyList<Profession> catalog)
    {
        var vm = new SchematicVm(t) { IdFollowsName = true };
        vm.SetProfessionCatalog(catalog);
        vm.Name = "New Schematic";
        return vm;
    }

    public static SchematicVm FromModel(Schematic m, IReadOnlyList<Profession> catalog, ChangeTracker t)
    {
        var vm = new SchematicVm(t)
        {
            _id = m.Id, _name = m.Name, _category = m.Category ?? "", _tab = m.Tab ?? "", _complexity = m.Complexity, _size = m.Size,
            _showIndex = m.ShowOverride == null ? 0 : m.ShowOverride.Value ? 1 : 2,
            _ignoreExperimentation = m.IgnoreExperimentation,
            _extraProfessions = m.Professions.ToList()
        };
        vm.SetProfessionCatalog(catalog);   // moves the listed IDs into check boxes, leaving the unknown ones in _extraProfessions
        foreach (var c in m.Components) vm.Components.Add(ComponentSlotVm.FromModel(c, t));
        foreach (var r in m.Resources) vm.Resources.Add(ResourceSlotVm.FromModel(r, t));
        foreach (var e in m.Experiments) vm.Experiments.Add(ExperimentVm.FromModel(e, t));
        foreach (var sk in m.Skills) vm.Skills.Add(SkillSlotVm.FromModel(sk, t));
        return vm;
    }

    public Schematic ToModel()
    {
        var m = new Schematic
        {
            Id = Id.Trim(), Name = Name.Trim(),
            Category = string.IsNullOrWhiteSpace(_category) ? null : _category.Trim(),
            Tab = string.IsNullOrWhiteSpace(_tab) ? null : _tab.Trim(),
            Complexity = _complexity, Size = _size,
            ShowOverride = _showIndex switch { 1 => true, 2 => false, _ => null },
            IgnoreExperimentation = _ignoreExperimentation,
        };
        m.Professions.AddRange(SelectedProfessionIds());
        foreach (var c in Components) m.Components.Add(c.ToModel());
        foreach (var r in Resources) m.Resources.Add(r.ToModel());
        foreach (var e in Experiments) m.Experiments.Add(e.ToModel());
        foreach (var sk in Skills) m.Skills.Add(sk.Skill.Trim());
        return m;
    }
}
