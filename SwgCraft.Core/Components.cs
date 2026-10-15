using System.Text.Json.Serialization;

namespace SwgCraft.Core;

// ---------------------------------------------------------------- looted items (lootitems.json)

public sealed class LootItemsDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<LootItem> Items { get; set; } = new();
}

/// <summary>
/// A looted component. Every looted item carries a serial number and can come in stacks (a stack of N allows a factory run of N-1).
/// Stacks and serials belong to inventory; this file only says which looted items exist. Modifiers are not tracked.
/// </summary>
public sealed class LootItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

// ---------------------------------------------------------------- component classes (componentclasses.json)

public enum MemberKind { Schematic, LootItem }

public sealed class ClassMember
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MemberKind Kind { get; set; }
    public string Id { get; set; } = "";
}

public sealed class ComponentClassesDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<ComponentClass> Classes { get; set; } = new();
}

/// <summary>
/// A set of interchangeable components, e.g. "Blade Vibro Unit" = [Blade Vibro Unit, Advanced Blade Vibro Unit, Light Weight Blade Vibro Unit (loot)].
/// Members are a flat list (no nesting). The first member is the base item.
/// </summary>
public sealed class ComponentClass
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ClassMember> Members { get; set; } = new();
}

// ---------------------------------------------------------------- schematics (schematics.json)

/// <summary>What a component slot points at: another schematic's product, any member of a component class, or a looted item.</summary>
public enum SlotKind { Schematic, Class, LootItem }

public sealed class SchematicsDocument
{
    /// <summary>
    /// 6: a component slot can be a looted item. 5: schematics can be marked to ignore experimentation in builds. 4: schematics have a crafting tab. 3: schematics list the skills that can craft them. 2: experiments hold a list of modifiers,
    /// each with its own percentages (1: one set of percentages per experiment). Older files still load.
    /// </summary>
    public const int CurrentSchemaVersion = 6;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<Schematic> Schematics { get; set; } = new();
}

public sealed class Schematic
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Profession IDs that can make this schematic (at least one).</summary>
    public List<string> Professions { get; set; } = new();

    /// <summary>Skill IDs (from professions.json) that can craft this schematic. Each skill's profession must also be in <see cref="Professions"/>.</summary>
    public List<string> Skills { get; set; } = new();

    /// <summary>ID of an entry in categories.json (older files may hold the category's name; the schematic editor converts those to the ID when it loads).</summary>
    public string? Category { get; set; }

    /// <summary>ID of an entry in tabs.json: the SWGEmu crafting tab the schematic is listed under. Optional.</summary>
    public string? Tab { get; set; }
    public int? Complexity { get; set; }
    public int? Size { get; set; }

    /// <summary>null = no override (default behaviour); true/false = JnF's "Show Item" flag.</summary>
    public bool? ShowOverride { get; set; }

    /// <summary>
    /// Builds ignore this schematic's experimentation, whatever the build's own setting. For schematics whose experimental lines
    /// exist but do nothing in game (e.g. Experimental Quality on most furniture). The experimentals are still kept. Not written when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IgnoreExperimentation { get; set; }

    /// <summary>
    /// Whether a build should apply this schematic's experimentation. The schematic's own <see cref="IgnoreExperimentation"/> wins:
    /// when it is set, experimentation is ignored even if the build would use it.
    /// </summary>
    public bool ExperimentationApplies(bool buildIgnoresExperimentation) =>
        !IgnoreExperimentation && !buildIgnoresExperimentation && Experiments.Count > 0;

    public List<ComponentSlot> Components { get; set; } = new();
    public List<ResourceSlot> Resources { get; set; } = new();
    public List<Experiment> Experiments { get; set; } = new();
}

/// <summary>A component the schematic consumes: another schematic's product, or any member of a component class.</summary>
public sealed class ComponentSlot
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SlotKind Kind { get; set; }

    /// <summary>Schematic ID, class ID or looted item ID, depending on <see cref="Kind"/>.</summary>
    public string Target { get; set; } = "";

    public int Quantity { get; set; } = 1;

    /// <summary>All units must come from one factory run (one shared serial). Only meaningful when Quantity is 2 or more.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Identical { get; set; }

    /// <summary>The schematic can be made without this component.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Optional { get; set; }
}

/// <summary>A resource the schematic consumes. Resource is a resource-tree node ID; an interior node means "any resource below it".</summary>
public sealed class ResourceSlot
{
    public string Resource { get; set; } = "";
    public int Amount { get; set; } = 1;
}

/// <summary>
/// One experimental: the line experimentation points are spent on (e.g. "Charges"). It raises one or more
/// modifiers of the item together, and each modifier has its own stat percentages, e.g.
/// Charges = Charges (OQ 67, UT 33) + Effective Range (OQ 67, CD 33).
/// </summary>
public sealed class Experiment
{
    public string Name { get; set; } = "";
    public List<ExperimentModifier> Modifiers { get; set; } = new();

    /// <summary>
    /// Only in schemaVersion 1 files: the experiment's own percentages. <see cref="SchematicUpgrade"/> turns them into a single
    /// modifier when the file is loaded, so this is normally null and is never written.
    /// </summary>
    [JsonPropertyName("weights")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<Stat, int>? LegacyWeights { get; set; }
}

/// <summary>An attribute of the item that an experimental changes (Charges, Effective Range, ...): whole-number percentages per stat that add up to exactly 100.</summary>
public sealed class ExperimentModifier
{
    public string Name { get; set; } = "";
    public SortedDictionary<Stat, int> Weights { get; set; } = new();
}

/// <summary>Brings schematics read from an older file up to the current shape, in memory.</summary>
public static class SchematicUpgrade
{
    /// <summary>
    /// schemaVersion 1 -> 2: each experiment's percentages become one modifier with the experiment's name.
    /// An experiment that has both old percentages and modifiers is left alone (the validator reports it) so nothing is lost.
    /// Returns true if anything was converted.
    /// </summary>
    public static bool Upgrade(SchematicsDocument doc)
    {
        bool changed = false;
        foreach (var s in doc.Schematics)
            foreach (var e in s.Experiments)
            {
                if (e.LegacyWeights == null || e.Modifiers.Count > 0) continue;
                e.Modifiers.Add(new ExperimentModifier { Name = e.Name, Weights = e.LegacyWeights });
                e.LegacyWeights = null;
                changed = true;
            }
        doc.SchemaVersion = SchematicsDocument.CurrentSchemaVersion;
        return changed;
    }
}

// ---------------------------------------------------------------- read-only lookups

public sealed class LootCatalog
{
    public static LootCatalog Empty { get; } = new(new LootItemsDocument());
    readonly Dictionary<string, LootItem> _byId = new(StringComparer.Ordinal);
    public LootItemsDocument Document { get; }
    public IReadOnlyList<LootItem> All => Document.Items;
    public LootCatalog(LootItemsDocument d)
    {
        Document = d;
        foreach (var i in d.Items) if (!string.IsNullOrEmpty(i.Id)) _byId.TryAdd(i.Id, i);
    }
    public LootItem? Find(string id) => _byId.GetValueOrDefault(id);
}

public sealed class ClassCatalog
{
    public static ClassCatalog Empty { get; } = new(new ComponentClassesDocument());
    readonly Dictionary<string, ComponentClass> _byId = new(StringComparer.Ordinal);
    public ComponentClassesDocument Document { get; }
    public IReadOnlyList<ComponentClass> All => Document.Classes;
    public ClassCatalog(ComponentClassesDocument d)
    {
        Document = d;
        foreach (var c in d.Classes) if (!string.IsNullOrEmpty(c.Id)) _byId.TryAdd(c.Id, c);
    }
    public ComponentClass? Find(string id) => _byId.GetValueOrDefault(id);
}

public sealed class SchematicCatalog
{
    public static SchematicCatalog Empty { get; } = new(new SchematicsDocument());
    readonly Dictionary<string, Schematic> _byId = new(StringComparer.Ordinal);
    public SchematicsDocument Document { get; }
    public IReadOnlyList<Schematic> All => Document.Schematics;
    public SchematicCatalog(SchematicsDocument d)
    {
        Document = d;
        foreach (var s in d.Schematics) if (!string.IsNullOrEmpty(s.Id)) _byId.TryAdd(s.Id, s);
    }
    public Schematic? Find(string id) => _byId.GetValueOrDefault(id);
}
