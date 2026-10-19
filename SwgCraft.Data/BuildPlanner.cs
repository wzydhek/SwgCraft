using SwgCraft.Core;

namespace SwgCraft.Data;

// ================================================================ inputs

/// <summary>Everything a build plan is worked out from. Inventories are read only; the plan never changes them.</summary>
public sealed record BuildInputs(
    SchematicCatalog Schematics,
    ClassCatalog Classes,
    LootCatalog Loot,
    ResourceTree Tree,
    IReadOnlyList<InventoryItem> ResourceInventory,
    IReadOnlyList<ItemStack> ItemInventory,
    IReadOnlyList<Spawn> CurrentSpawns,
    Func<string, bool> ProfessionEnabled);

/// <param name="IgnoreExperimentation">The build-level toggle; a schematic's own IgnoreExperimentation always wins (see Schematic.ExperimentationApplies).</param>
/// <param name="IncludeOptional">Plan optional component slots too (they are skipped otherwise).</param>
/// <param name="ForceCraft">Item keys (see BuildItem.Key) to craft even if item inventory has them.</param>
/// <param name="FactoryRunsByDefault">Make every crafted item in factory run(s) unless toggled.</param>
/// <param name="FactoryToggle">Item keys that do the opposite of <paramref name="FactoryRunsByDefault"/>. Ignored where a factory run is required.</param>
/// <param name="OptionalToggle">Slot keys (see BuildPlanner.SlotKey) of optional slots that do the opposite of <paramref name="IncludeOptional"/>.</param>
/// <param name="ClassChoice">Slot key -> the class member to use there (an item key: "schematic:id" or "loot:id"). Without a
/// choice a class slot takes any member from inventory and crafts the rest from the class's default recipe.</param>
public sealed record BuildOptions(
    bool IgnoreExperimentation = false,
    bool IncludeOptional = false,
    IReadOnlySet<string>? ForceCraft = null,
    bool FactoryRunsByDefault = false,
    IReadOnlySet<string>? FactoryToggle = null,
    IReadOnlySet<string>? OptionalToggle = null,
    IReadOnlyDictionary<string, string>? ClassChoice = null);

// ================================================================ outputs

public enum BuildItemKind { Schematic, LootItem, Class }

/// <summary>Units taken from one item stack.</summary>
public sealed record ItemTake(ItemStack Stack, int Quantity);

/// <summary>
/// Units of a resource taken from one inventory stack for one resource slot. <see cref="Crafts"/> is the number of units made
/// with it (by hand), or the run size (factory: the take also covers one more unit's worth for the manufacturing schematic).
/// </summary>
/// <param name="Specific">The spawn is of a type some schematic wants specifically (see What to Harvest), used here only
/// because nothing general-purpose was held.</param>
public sealed record ResourceTake(InventoryItem Stack, int Quantity, double Value, double? CapPercent, int Crafts, bool Specific = false)
{
    public double? Cost => Stack.PricePerUnit is double p ? p * Quantity : null;
}

/// <summary>A current spawn that could fill a shortage.</summary>
public sealed record ShortageSuggestion(Spawn Spawn, double Value, double? CapPercent);

/// <summary>One resource slot of a crafted item: how much it needs in total and where it comes from.</summary>
public sealed class ResourceSlotPlan
{
    public int SlotIndex { get; init; }
    /// <summary>The slot's name in the game (null if the schematic doesn't have one).</summary>
    public string? Name { get; init; }
    public string ResourceId { get; init; } = "";
    /// <summary>Amount per unit crafted.</summary>
    public int AmountEach { get; init; }
    public int Crafts { get; init; }
    /// <summary>Factory runs: one manufacturing schematic per run, each using one more unit's worth.</summary>
    public int Setups { get; set; }
    public int Needed => AmountEach * (Crafts + Setups);
    public bool QualityMatters { get; init; }
    /// <summary>
    /// The slot takes a group ("any") or quality doesn't matter, so spawns of types other schematics want specifically are
    /// kept back: used only when nothing else is held, the least valuable of them first.
    /// </summary>
    public bool KeepsSpecificBack { get; init; }
    /// <summary>Factory: take i is run i (the first Runs.Count takes). Hand crafts (all of a hand-crafted item, the odd ones of a factory item) follow.</summary>
    public List<ResourceTake> Takes { get; } = new();
    /// <summary>Units this slot can't be filled for.</summary>
    public int ShortCrafts { get; set; }
    /// <summary>Manufacturing schematics (one unit's worth each) that can't be filled.</summary>
    public int ShortSetups { get; set; }
    public int ShortAmount => (ShortCrafts + ShortSetups) * AmountEach;
    public List<ShortageSuggestion> Suggestions { get; } = new();
}

/// <summary>
/// Some of an item's demand. An identical demand is one group: all its units must be identical (one serial - one factory run
/// or one stack with a serial). A factory-run user makes every slot identical for each of its runs, and also needs one more
/// set for its manufacturing schematic.
/// </summary>
public sealed class BuildDemand
{
    public BuildDemand(BuildItem from, int units, bool identical, bool fromFactory, int parentRun = -1, int slotIndex = -1)
    {
        From = from; Units = units; Identical = identical; FromFactory = fromFactory; ParentRun = parentRun; SlotIndex = slotIndex;
    }
    public BuildItem From { get; }
    public int Units { get; }
    public bool Identical { get; }
    public int GroupSize => Identical ? Units : 1;
    public bool FromFactory { get; }
    /// <summary>The user's factory run this group is for (-1 when hand-crafted).</summary>
    public int ParentRun { get; }
    /// <summary>The user's component slot (-1 for a class's recipe).</summary>
    public int SlotIndex { get; }

    /// <summary>The group came whole from this stack.</summary>
    public ItemStack? FilledFrom { get; internal set; }
    /// <summary>The group is made in this run of the item.</summary>
    public int? MadeInRun { get; internal set; }
    /// <summary>A class's group, handed on to the recipe as this demand.</summary>
    public BuildDemand? ForwardedAs { get; internal set; }
}

/// <summary>A component the item's schematic uses, merged over its slots: <see cref="PerUnit"/> units per unit crafted.</summary>
public sealed record BuildComponent(BuildItem Child, int PerUnit, bool Identical, int Slots);

/// <summary>One component slot of a crafted item.</summary>
public sealed record BuildSlotUse(int SlotIndex, BuildItem Child, int PerUnit, bool Identical, string? Name = null);

/// <summary>
/// A component slot of a schematic as planned, for choosing: whether an optional slot is used, and for a class slot which
/// member fills it (<see cref="ChosenMember"/>, an item key; null = any member, crafted from the class's default recipe).
/// </summary>
public sealed record BuildSlotChoice(int SlotIndex, string Key, ComponentSlot Slot, string TargetLabel, bool Included, BuildItem Child,
                                     string? ClassId, string? ChosenMember);

/// <summary>A factory run: its size (not counting the manufacturing schematic) and the identical groups it makes.</summary>
public sealed record FactoryRunPlan(int Size, IReadOnlyList<BuildDemand> Groups);

/// <summary>
/// One kind of thing the build needs - a schematic's product, a looted item or a component class - with every demand for it
/// added up (two slots needing 1 and 6 wall modules = 7 wall modules), what comes from item inventory, and what is crafted.
/// </summary>
public sealed class BuildItem
{
    public string Key { get; init; } = "";
    public BuildItemKind ItemKind { get; init; }
    public string Id { get; init; } = "";
    public string Label { get; set; } = "";
    public bool IsTarget { get; init; }
    /// <summary>Depth below the target (the deepest place it is used).</summary>
    public int Level { get; set; }

    /// <summary>The schematic crafted (Schematic kind).</summary>
    public Schematic? Schematic { get; set; }
    /// <summary>For a class: the member schematic its crafted part is made from.</summary>
    public BuildItem? Recipe { get; set; }

    public List<BuildDemand> Demands { get; } = new();
    public int Needed => Demands.Sum(d => d.Units);

    public List<ItemTake> FromInventory { get; } = new();
    public int FromInventoryCount => FromInventory.Sum(t => t.Quantity);
    /// <summary>Units crafted (Schematic kind) or handed on to the recipe (Class kind).</summary>
    public int CraftCount { get; set; }
    public int MissingCount { get; set; }
    public string? Problem { get; set; }
    public bool CanForceCraft { get; set; }

    public List<BuildComponent> Components { get; } = new();
    public List<BuildSlotUse> Slots { get; } = new();
    /// <summary>Every component slot of the schematic (optional ones too), with what fills it.</summary>
    public List<BuildSlotChoice> SlotChoices { get; } = new();
    public List<string> SkippedOptional { get; } = new();
    public List<BuildItem> UsedBy => Demands.Select(d => d.From).Where(f => f != this).Distinct().ToList();

    public bool QualityMatters { get; set; }
    public bool FactoryRun { get; set; }
    /// <summary>A factory run can't be switched off here: identical units are needed (an identical slot, or a factory-run user).</summary>
    public bool FactoryRequired { get; set; }
    public string? FactoryReason { get; set; }
    public List<FactoryRunPlan> Runs { get; } = new();
    /// <summary>
    /// Factory item: units crafted by hand instead, because a run of one would waste a unit's worth on its manufacturing
    /// schematic (a lone unit left over, or stock too split for a bigger run).
    /// </summary>
    public int HandUnits { get; set; }
    /// <summary>Units crafted by hand (all of them for a hand-crafted item).</summary>
    public int ByHand => FactoryRun ? HandUnits : CraftCount;
    /// <summary>
    /// The looted parts (or classes with nothing craftable) that keep this item from being factory-made: a run needs an
    /// identical set of every component. Null when nothing does.
    /// </summary>
    public string? FactoryBlockedBy { get; set; }
    /// <summary>Factory units that no run could be formed for with the resources held (they are short, see the resource slots).</summary>
    public int UnmadeInRuns => FactoryRun ? CraftCount - Runs.Sum(r => r.Size) - HandUnits : 0;
    public List<ResourceSlotPlan> Resources { get; } = new();
}

public sealed record BuildPlan(BuildItem Target, IReadOnlyList<BuildItem> Items, BuildOptions Options)
{
    public IEnumerable<ResourceSlotPlan> ResourceSlots => Items.SelectMany(n => n.Resources);
    public bool Complete => Items.All(n => n.MissingCount == 0 && n.UnmadeInRuns == 0) && ResourceSlots.All(r => r.ShortCrafts == 0 && r.ShortSetups == 0);
    public double KnownCost => ResourceSlots.SelectMany(r => r.Takes).Sum(t => t.Cost ?? 0);
    public bool CostIsComplete => ResourceSlots.SelectMany(r => r.Takes).All(t => t.Cost != null);
}

// ================================================================ planner

/// <summary>
/// Works out how to build N of a schematic from what is held, by kind of item rather than by slot. Items are settled after
/// everything that uses them (users first), each in turn:
/// <list type="number">
/// <item>All demand for the item is added up across the build (two slots wanting 1 and 6 wall modules = 7).</item>
/// <item>Item inventory first: identical groups only whole from one stack with a serial; the rest crafted. Looted items can't be
///       crafted (missing); a class hands its remainder to one member schematic (an enabled profession's first).</item>
/// <item>Resources: best score where experimentation applies; for an "any" slot or where quality doesn't matter, spawns of
///       types other schematics want specifically are kept back (used last, least valuable first), and without
///       experimentation the biggest general-purpose stack goes first (Generic-marked first); hand crafts take each unit's amount
///       from one spawn. A factory run of R takes (R + 1) x amount from one spawn per slot - the extra unit's worth goes into the
///       manufacturing schematic. Filtered spawns are never used. Shortages get spawning suggestions.</item>
/// <item>Demand for its components: by hand, slot quantity per unit (identical slots in groups of the slot quantity). In a
///       factory run every slot becomes identical: run i needs one identical group of quantity x (R_i + 1) per slot - the
///       components for the run plus the set used to make the manufacturing schematic. So everything below a factory run is
///       factory-made too (or comes whole from one serial stack).</item>
/// </list>
/// </summary>
public static class BuildPlanner
{
    public static BuildPlan Plan(BuildInputs inputs, string schematicId, int quantity, BuildOptions? options = null)
    {
        options ??= new BuildOptions();
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity), "Plan at least one item.");
        var st = new State(inputs, options);

        var target = st.Get(BuildItemKind.Schematic, schematicId, isTarget: true);
        target.Demands.Add(new BuildDemand(target, quantity, false, false));

        // the item graph (static), checked for loops, in an order where every item comes after all its users
        var order = TopologicalOrder(st, target);

        // each item: inventory, craft / hand on / missing, factory decision, resources and runs, then demand for its parts
        foreach (var item in order)
        {
            Settle(st, item);
            AllocateResources(st, item);
            PushDemand(st, item);
        }

        var items = order.Where(i => i.IsTarget || i.Needed > 0).ToList();
        return new BuildPlan(target, items, options);
    }

    sealed class State
    {
        public readonly BuildInputs In;
        public readonly BuildOptions Options;
        public readonly Dictionary<string, BuildItem> Items = new(StringComparer.Ordinal);
        public readonly Dictionary<long, int> ItemLeft = new();       // item stack id -> units still free
        public readonly Dictionary<long, int> ResourceLeft = new();   // resource stack id -> units still free
        public readonly Dictionary<BuildItem, List<(BuildItem Child, ComponentSlot Slot)>> Edges = new();
        public readonly Dictionary<BuildItem, Need> Needs = new();   // what is left to craft, by identical group
        Dictionary<string, List<SortedDictionary<Stat, int>>>? _specific;

        /// <summary>
        /// For a spawn of a type that an enabled schematic names specifically for experimentation (a bold line in What to
        /// Harvest): its best score for those uses. Null for a general-purpose spawn.
        /// </summary>
        public double? SpecificValue(Spawn spawn)
        {
            _specific ??= HarvestPlanner.SpecificNeeds(In.Schematics.All, In.ProfessionEnabled, In.Tree);
            return _specific.TryGetValue(spawn.ResourceTypeId, out var ws) ? ws.Max(w => HarvestPlanner.Score(spawn.Stats, w)) : null;
        }

        public State(BuildInputs inputs, BuildOptions options)
        {
            In = inputs;
            Options = options;
            foreach (var s in inputs.ItemInventory) ItemLeft[s.Id] = s.Quantity;
            foreach (var r in inputs.ResourceInventory) ResourceLeft[r.Id] = r.Quantity;
        }

        public BuildItem Get(BuildItemKind kind, string id, bool isTarget = false)
        {
            var key = KeyFor(kind, id);
            if (Items.TryGetValue(key, out var item)) return item;
            item = new BuildItem { Key = key, ItemKind = kind, Id = id, IsTarget = isTarget };
            switch (kind)
            {
                case BuildItemKind.Schematic:
                    item.Schematic = In.Schematics.Find(id);
                    item.Label = item.Schematic is { } s ? NameOf(s) : id;
                    break;
                case BuildItemKind.LootItem:
                    item.Label = In.Loot.Find(id) is { } l ? (string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name.Trim()) : id;
                    break;
                default:
                    item.Label = In.Classes.Find(id) is { } c ? (string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name.Trim()) : id;
                    break;
            }
            Items[key] = item;
            return item;
        }
    }

    public static string KeyFor(BuildItemKind kind, string id) => kind switch
    {
        BuildItemKind.LootItem => "loot:" + id,
        BuildItemKind.Class => "class:" + id,
        _ => "schematic:" + id,
    };

    static string NameOf(Schematic s) => string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name.Trim();

    /// <summary>A schematic's component slot, for BuildOptions.OptionalToggle and ClassChoice.</summary>
    public static string SlotKey(string schematicId, int slotIndex) => $"{schematicId}#{slotIndex}";

    /// <summary>An item key for a class member.</summary>
    public static string MemberKey(MemberKind kind, string id) => KeyFor(kind == MemberKind.LootItem ? BuildItemKind.LootItem : BuildItemKind.Schematic, id);

    // ------------------------------------------------------------ the item graph

    /// <summary>Builds the edges from the target down, cuts loops (the item that would need itself is marked), and returns users before parts.</summary>
    static List<BuildItem> TopologicalOrder(State st, BuildItem target)
    {
        var visiting = new HashSet<BuildItem>();
        var done = new HashSet<BuildItem>();
        var post = new List<BuildItem>();

        void Visit(BuildItem item, int depth)
        {
            item.Level = Math.Max(item.Level, depth);
            if (done.Contains(item))
            {
                foreach (var (c, _) in st.Edges[item]) Relevel(c, depth + 1, new HashSet<BuildItem>());
                return;
            }
            visiting.Add(item);
            var edges = new List<(BuildItem, ComponentSlot)>();
            st.Edges[item] = edges;

            foreach (var (child, slot) in ChildrenOf(st, item))
            {
                if (visiting.Contains(child))
                {
                    item.Problem = $"'{child.Label}' would need itself to be built.";
                    continue;
                }
                edges.Add((child, slot));
                Visit(child, depth + 1);
            }
            visiting.Remove(item);
            done.Add(item);
            post.Add(item);
        }

        void Relevel(BuildItem item, int depth, HashSet<BuildItem> seen)
        {
            if (!seen.Add(item) || item.Level >= depth) return;
            item.Level = depth;
            if (st.Edges.TryGetValue(item, out var edges))
                foreach (var (c, _) in edges) Relevel(c, depth + 1, seen);
        }

        Visit(target, 0);
        post.Reverse();   // reverse post-order: every item after all the items that use it
        return post;
    }

    /// <summary>
    /// The direct parts of an item: a schematic's component slots (optional ones only if switched on; a class slot filled by the
    /// chosen member if there is one), a class's recipe.
    /// </summary>
    static IEnumerable<(BuildItem Child, ComponentSlot Slot)> ChildrenOf(State st, BuildItem item)
    {
        if (item.ItemKind == BuildItemKind.Schematic && item.Schematic is { } s)
        {
            for (int i = 0; i < s.Components.Count; i++)
            {
                var slot = s.Components[i];
                var key = SlotKey(s.Id, i);
                var kind = slot.Kind switch { SlotKind.Class => BuildItemKind.Class, SlotKind.LootItem => BuildItemKind.LootItem, _ => BuildItemKind.Schematic };
                var target = st.Get(kind, slot.Target);
                var child = target;
                string? chosen = null;
                if (kind == BuildItemKind.Class && st.Options.ClassChoice?.GetValueOrDefault(key) is { Length: > 0 } pick
                    && st.In.Classes.Find(slot.Target) is { } cls && cls.Members.Any(x => MemberKey(x.Kind, x.Id) == pick))
                {
                    chosen = pick;
                    var m = cls.Members.First(x => MemberKey(x.Kind, x.Id) == pick);
                    child = st.Get(m.Kind == MemberKind.LootItem ? BuildItemKind.LootItem : BuildItemKind.Schematic, m.Id);
                }
                bool included = !slot.Optional || (st.Options.IncludeOptional ^ (st.Options.OptionalToggle?.Contains(key) == true));
                item.SlotChoices.Add(new BuildSlotChoice(i, key, slot, target.Label, included, child,
                                                         kind == BuildItemKind.Class ? slot.Target : null, chosen));
                if (!included)
                {
                    item.SkippedOptional.Add(child.Label);
                    continue;
                }
                yield return (child, slot);
            }
        }
        else if (item.ItemKind == BuildItemKind.Class && ChooseRecipe(st, item.Id) is { } recipe)
        {
            var r = st.Get(BuildItemKind.Schematic, recipe.Id);
            item.Recipe = r;
            yield return (r, new ComponentSlot { Kind = SlotKind.Schematic, Target = recipe.Id, Quantity = 1 });
        }
    }

    /// <summary>The schematic a class's crafted part is made from: the first member schematic of an enabled profession, else the first.</summary>
    static Schematic? ChooseRecipe(State st, string classId)
    {
        if (st.In.Classes.Find(classId) is not { } cls) return null;
        var candidates = cls.Members.Where(m => m.Kind == MemberKind.Schematic)
                                    .Select(m => st.In.Schematics.Find(m.Id)).OfType<Schematic>().ToList();
        return candidates.FirstOrDefault(s => s.Professions.Any(p => st.In.ProfessionEnabled(p))) ?? candidates.FirstOrDefault();
    }

    // ------------------------------------------------------------ settling an item

    /// <summary>What is still needed after item inventory: identical groups and loose units.</summary>
    sealed class Need
    {
        public readonly List<BuildDemand> Groups = new();
        public int Loose;
        public bool FromFactory;
        public int Total => Groups.Sum(g => g.Units) + Loose;
    }

    static void Settle(State st, BuildItem item)
    {
        if (item.Needed == 0) return;

        var need = new Need { FromFactory = item.Demands.Any(d => d.FromFactory) };
        foreach (var d in item.Demands)
        {
            if (d.Identical && d.Units >= 2) need.Groups.Add(d);
            else need.Loose += d.Units;
        }

        // 1. item inventory (never for the target; not when forced to craft)
        var sources = InventorySources(st, item);
        item.CanForceCraft = item.ItemKind != BuildItemKind.LootItem && (item.ItemKind == BuildItemKind.Class ? item.Recipe != null : item.Schematic != null);
        bool forced = st.Options.ForceCraft?.Contains(item.Key) == true;
        if (!item.IsTarget && !forced) TakeFromInventory(st, item, need, sources);

        int rest = need.Total;
        if (rest == 0) return;

        // 2. the rest
        switch (item.ItemKind)
        {
            case BuildItemKind.LootItem:
                item.MissingCount = rest;
                item.Problem = $"{rest:N0} more needed" + (need.Groups.Count > 0 ? " (identical units, so only whole groups from one serial count)" : "") +
                               ": looted items can't be crafted.";
                return;

            case BuildItemKind.Class:
                if (item.Recipe == null || st.Edges[item].Count == 0)
                {
                    item.MissingCount = rest;
                    item.Problem ??= st.In.Classes.Find(item.Id) == null
                        ? $"Component class '{item.Id}' does not exist."
                        : $"{rest:N0} more needed and no member can be crafted.";
                    return;
                }
                item.CraftCount = rest;   // handed on to the recipe, with the identical groups kept
                foreach (var g in need.Groups)
                {
                    var fwd = new BuildDemand(item, g.Units, true, g.FromFactory);
                    g.ForwardedAs = fwd;
                    item.Recipe.Demands.Add(fwd);
                }
                if (need.Loose > 0) item.Recipe.Demands.Add(new BuildDemand(item, need.Loose, false, need.FromFactory));
                return;
        }

        if (item.Schematic is not { } s)
        {
            item.MissingCount = rest;
            item.Problem = $"Schematic '{item.Id}' does not exist.";
            return;
        }
        if (item.Problem != null && st.Edges[item].Count < item.SlotChoices.Count(c => c.Included))
        {
            item.MissingCount = rest;   // part of a loop
            return;
        }

        item.CraftCount = rest;
        item.QualityMatters = s.ExperimentationApplies(st.Options.IgnoreExperimentation);

        // factory decision
        bool identical = need.Groups.Count > 0;
        bool chosen = st.Options.FactoryRunsByDefault ^ (st.Options.FactoryToggle?.Contains(item.Key) == true);
        item.FactoryRequired = identical;
        // a factory run needs identical sets of every component: a looted part (or a class with nothing craftable) can't be one
        var blockers = st.Edges[item].Select(e => e.Child)
            .Where(c => c.ItemKind == BuildItemKind.LootItem || (c.ItemKind == BuildItemKind.Class && c.Recipe == null))
            .Select(c => c.Label).Distinct().ToList();
        item.FactoryBlockedBy = blockers.Count > 0 ? string.Join(", ", blockers) : null;
        item.FactoryRun = identical || (chosen && rest >= 2 && blockers.Count == 0);   // a run of one only wastes a unit's worth on the schematic
        var factoryUsers = need.Groups.Where(g => g.FromFactory).Select(g => g.From.Label).Distinct().ToList();
        item.FactoryReason = factoryUsers.Count > 0
                ? $"a factory run of {string.Join(", ", factoryUsers)} needs identical components (one set per run plus one for its manufacturing schematic)"
            : identical ? "identical units are needed"
            : item.FactoryRun ? "chosen" : null;
        st.Needs[item] = need;
    }

    /// <summary>Demand for the item's components, once its runs are known.</summary>
    static void PushDemand(State st, BuildItem item)
    {
        if (item.ItemKind != BuildItemKind.Schematic || item.Schematic is not { } s || item.CraftCount == 0) return;

        foreach (var group in st.Edges[item].GroupBy(e => e.Child))
            item.Components.Add(new BuildComponent(group.Key, group.Sum(e => Math.Max(1, e.Slot.Quantity)),
                                                   item.FactoryRun || group.Any(e => e.Slot.Identical && e.Slot.Quantity >= 2), group.Count()));

        // factory runs (and a pretend run for units no run could be formed for, so their parts still show), then hand crafts
        var runs = item.FactoryRun ? item.Runs.Select(r => r.Size).ToList() : new List<int>();
        if (item.UnmadeInRuns > 0) runs.Add(item.UnmadeInRuns);
        int hand = item.ByHand;

        foreach (var (child, slot) in st.Edges[item])
        {
            int q = Math.Max(1, slot.Quantity);
            int index = s.Components.IndexOf(slot);
            item.Slots.Add(new BuildSlotUse(index, child, q, item.FactoryRun || (slot.Identical && q >= 2), slot.Name));

            for (int r = 0; r < runs.Count; r++)
                child.Demands.Add(new BuildDemand(item, q * (runs[r] + 1), identical: true, fromFactory: true, parentRun: r, slotIndex: index));
            if (hand == 0) continue;
            if (slot.Identical && q >= 2)
                for (int u = 0; u < hand; u++)
                    child.Demands.Add(new BuildDemand(item, q, identical: true, fromFactory: false, slotIndex: index));
            else
                child.Demands.Add(new BuildDemand(item, q * hand, identical: false, fromFactory: false, slotIndex: index));
        }
    }

    /// <summary>The item stacks that can stand for this item: its own, or (a class) any member's in member order.</summary>
    static List<ItemStack> InventorySources(State st, BuildItem item)
    {
        var members = new List<(MemberKind Kind, string Id)>();
        switch (item.ItemKind)
        {
            case BuildItemKind.Schematic: members.Add((MemberKind.Schematic, item.Id)); break;
            case BuildItemKind.LootItem: members.Add((MemberKind.LootItem, item.Id)); break;
            default:
                if (st.In.Classes.Find(item.Id) is { } cls) members.AddRange(cls.Members.Select(m => (m.Kind, m.Id)));
                break;
        }
        return members.SelectMany(m => st.In.ItemInventory.Where(s => s.Kind == m.Kind && s.ItemId == m.Id)).ToList();
    }

    /// <summary>
    /// Identical groups first (biggest first), each from one stack with a serial that still has the whole group (the smallest
    /// such stack, keeping big ones for big groups); then loose units, from stacks without a serial first, then the rest.
    /// </summary>
    static void TakeFromInventory(State st, BuildItem item, Need need, List<ItemStack> sources)
    {
        var taken = new Dictionary<long, int>();
        void Take(ItemStack s, int n)
        {
            st.ItemLeft[s.Id] = st.ItemLeft.GetValueOrDefault(s.Id) - n;
            taken[s.Id] = taken.GetValueOrDefault(s.Id) + n;
        }

        need.Groups.Sort((a, b) => b.Units.CompareTo(a.Units));
        for (int i = 0; i < need.Groups.Count; i++)
        {
            var g = need.Groups[i];
            var stack = sources.Where(s => s.Serial != null && st.ItemLeft.GetValueOrDefault(s.Id) >= g.Units)
                               .OrderBy(s => st.ItemLeft.GetValueOrDefault(s.Id)).FirstOrDefault();
            if (stack == null) continue;
            Take(stack, g.Units);
            g.FilledFrom = stack;
            need.Groups.RemoveAt(i--);
        }

        foreach (var s in sources.OrderBy(s => s.Serial == null ? 0 : 1).ThenByDescending(s => st.ItemLeft.GetValueOrDefault(s.Id)))
        {
            if (need.Loose == 0) break;
            int n = Math.Min(need.Loose, st.ItemLeft.GetValueOrDefault(s.Id));
            if (n <= 0) continue;
            Take(s, n);
            need.Loose -= n;
        }

        foreach (var s in sources.Where(s => taken.ContainsKey(s.Id)))
            item.FromInventory.Add(new ItemTake(s, taken[s.Id]));
    }

    // ------------------------------------------------------------ resources

    static void AllocateResources(State st, BuildItem item)
    {
        if (item.ItemKind != BuildItemKind.Schematic || item.Schematic is not { } s || item.CraftCount == 0) return;
        var modifiers = s.Experiments.SelectMany(e => e.Modifiers).Where(m => m.Weights.Values.Any(w => w > 0)).ToList();

        var slots = new List<(ResourceSlotPlan Plan, List<(InventoryItem Item, double Value)> Ordered)>();
        for (int i = 0; i < s.Resources.Count; i++)
        {
            var slot = s.Resources[i];
            var res = slot.Resource?.Trim() ?? "";
            bool quality = item.QualityMatters && modifiers.Count > 0;
            var plan = new ResourceSlotPlan
            {
                SlotIndex = i, Name = slot.Name, ResourceId = res, AmountEach = Math.Max(1, slot.Amount), Crafts = item.CraftCount,
                QualityMatters = quality,
                KeepsSpecificBack = !quality || st.In.Tree.Find(res) is { IsLeaf: false },
            };
            item.Resources.Add(plan);

            var stock = st.In.ResourceInventory
                .Where(r => !r.Spawn.Filtered && res.Length > 0 && st.In.Tree.IsDescendantOrSelf(r.Spawn.ResourceTypeId, res))
                .Select(r => (Item: r, Value: SlotScore(r.Spawn.Stats, modifiers)))
                .ToList();
            int Left((InventoryItem Item, double Value) x) => st.ResourceLeft.GetValueOrDefault(x.Item.Id);

            List<(InventoryItem Item, double Value)> ordered;
            if (!plan.KeepsSpecificBack)
                // a specific type with experimentation: the best spawn
                ordered = stock.OrderByDescending(x => x.Value).ThenByDescending(Left).ToList();
            else
            {
                // general-purpose spawns first: the best one if quality matters, else the biggest stack (spawns marked
                // Generic first, cheapest on a tie); then spawns other schematics want specifically, least valuable first
                var tagged = stock.Select(x => (X: x, Spec: st.SpecificValue(x.Item.Spawn))).ToList();
                var general = tagged.Where(t => t.Spec == null).Select(t => t.X);
                general = quality
                    ? general.OrderByDescending(x => x.Value).ThenByDescending(Left)
                    : general.OrderByDescending(x => x.Item.Spawn.Generic).ThenByDescending(Left).ThenBy(x => x.Item.PricePerUnit ?? double.MaxValue);
                var specific = tagged.Where(t => t.Spec != null).OrderBy(t => t.Spec).ThenByDescending(t => Left(t.X)).Select(t => t.X);
                ordered = general.Concat(specific).ToList();
            }
            slots.Add((plan, ordered));
        }

        if (item.FactoryRun) AllocateFactoryRuns(st, item, slots, modifiers);
        else foreach (var (plan, ordered) in slots) AllocateByHand(st, plan, ordered, modifiers, item.CraftCount);

        foreach (var (plan, _) in slots)
        {
            if (plan.KeepsSpecificBack)
                for (int t = 0; t < plan.Takes.Count; t++)
                    if (st.SpecificValue(plan.Takes[t].Stack.Spawn) != null) plan.Takes[t] = plan.Takes[t] with { Specific = true };
            if (plan.ShortCrafts > 0) Suggest(st, plan, modifiers);
        }
    }

    /// <summary>Hand crafting: every unit takes the slot's amount from one spawn, best (or cheapest) spawn first.</summary>
    static void AllocateByHand(State st, ResourceSlotPlan plan, List<(InventoryItem Item, double Value)> ordered, List<ExperimentModifier> modifiers, int units)
    {
        int left = units;
        foreach (var (stack, value) in ordered)
        {
            if (left == 0) break;
            int free = st.ResourceLeft.GetValueOrDefault(stack.Id);
            int crafts = Math.Min(free / plan.AmountEach, left);
            if (crafts <= 0) continue;
            st.ResourceLeft[stack.Id] = free - crafts * plan.AmountEach;
            left -= crafts;
            plan.Takes.Add(new ResourceTake(stack, crafts * plan.AmountEach, value, CapPercent(stack.Spawn, modifiers, st.In.Tree), crafts));
        }
        plan.ShortCrafts += left;
    }

    /// <summary>
    /// Factory runs. A run of R units needs (R + 1) x amount from one spawn for every slot: one unit's worth goes into the
    /// manufacturing schematic, and the factory only takes the same spawn. Each run is as big as the stock allows and is packed
    /// with whole identical groups (biggest first) and then loose units. Each slot then uses its most preferred spawn that
    /// covers the run.
    /// </summary>
    static void AllocateFactoryRuns(State st, BuildItem item, List<(ResourceSlotPlan Plan, List<(InventoryItem Item, double Value)> Ordered)> slots,
                                    List<ExperimentModifier> modifiers)
    {
        var need = st.Needs.TryGetValue(item, out var n) ? n : new Need { Loose = item.CraftCount };
        var groups = need.Groups.OrderByDescending(g => g.Units).ToList();
        int loose = need.Loose;
        // the biggest run this stack could supply on its own (one unit's worth is kept for the manufacturing schematic)
        int Cap((InventoryItem Item, double Value) c, int each) => st.ResourceLeft.GetValueOrDefault(c.Item.Id) / each - 1;

        // For a run of the given size, one spawn per slot (slots of the same resource may share a spawn if it has enough for both).
        List<(InventoryItem Item, double Value)>? Pick(int size)
        {
            var used = new Dictionary<long, int>();
            var picks = new List<(InventoryItem Item, double Value)>();
            foreach (var (plan, ordered) in slots)
            {
                int units = (size + 1) * plan.AmountEach;
                var c = ordered.FirstOrDefault(o => st.ResourceLeft.GetValueOrDefault(o.Item.Id) - used.GetValueOrDefault(o.Item.Id) >= units);
                if (c.Item == null) return null;
                used[c.Item.Id] = used.GetValueOrDefault(c.Item.Id) + units;
                picks.Add(c);
            }
            return picks;
        }

        int hand = 0;
        while (groups.Count > 0 || loose > 0)
        {
            bool runOfOne = false;
            long max = slots.Count == 0 ? int.MaxValue
                     : slots.Min(x => x.Ordered.Count == 0 ? 0 : x.Ordered.Max(c => Cap(c, x.Plan.AmountEach)));
            List<BuildDemand> packed;
            int size, extra;
            List<(InventoryItem Item, double Value)>? picks;
            while (true)
            {
                packed = new List<BuildDemand>();
                size = 0;
                foreach (var g in groups)
                    if (size + g.Units <= max) { size += g.Units; packed.Add(g); }
                extra = (int)Math.Max(0, Math.Min(loose, max - size));
                size += extra;
                // nothing bigger than a run of one fits (groups are 2+, so only loose units are left that could fit):
                // loose units are better crafted by hand, which needs no extra unit's worth for a manufacturing schematic
                if (size <= 1) { picks = null; runOfOne = loose > 0; break; }
                picks = Pick(size);
                if (picks != null) break;
                max = size - 1;   // slots competing for one spawn: try a smaller run
            }
            if (runOfOne) { hand = loose; loose = 0; break; }   // runs of one waste a unit's worth: craft these by hand
            if (picks == null) break;

            int runIndex = item.Runs.Count;
            foreach (var g in packed) { groups.Remove(g); g.MadeInRun = runIndex; }
            loose -= extra;
            for (int k = 0; k < slots.Count; k++)
            {
                var plan = slots[k].Plan;
                var (stack, value) = picks[k];
                int units = (size + 1) * plan.AmountEach;
                st.ResourceLeft[stack.Id] = st.ResourceLeft.GetValueOrDefault(stack.Id) - units;
                plan.Takes.Add(new ResourceTake(stack, units, value, CapPercent(stack.Spawn, modifiers, st.In.Tree), size));
                plan.Setups++;
            }
            item.Runs.Add(new FactoryRunPlan(size, packed));
        }

        if (hand > 0)
        {
            item.HandUnits = hand;
            foreach (var (plan, ordered) in slots) AllocateByHand(st, plan, ordered, modifiers, hand);
        }

        int rest = groups.Sum(g => g.Units) + loose;
        if (rest > 0)
        {
            int smallest = groups.Count > 0 ? groups.Min(g => g.Units) : 1;
            var lacking = slots.Where(x => !x.Ordered.Any(c => Cap(c, x.Plan.AmountEach) >= smallest)).Select(x => x.Plan).ToList();
            foreach (var p in lacking.Count > 0 ? lacking : slots.Select(x => x.Plan))
            {
                p.ShortCrafts += rest;
                p.ShortSetups = 1;
                p.Setups++;
            }
            item.Problem = $"{rest:N0} unit(s) need more resources for a factory run: no single spawn held has enough for " +
                           (lacking.Count > 0 ? string.Join(", ", lacking.Select(p => $"slot {p.SlotIndex + 1}" + (string.IsNullOrWhiteSpace(p.Name) ? "" : $" ({p.Name.Trim()})"))) : "every slot at once") +
                           " (one spawn per slot covers the whole run plus one unit's worth for the manufacturing schematic" +
                           (groups.Count > 0 ? $"; identical groups of {string.Join(", ", groups.Select(g => g.Units).Distinct())} must stay in one run)." : ").");
        }
    }

    static void Suggest(State st, ResourceSlotPlan plan, List<ExperimentModifier> modifiers)
    {
        var res = plan.ResourceId;
        if (res.Length == 0) return;
        var held = st.In.ResourceInventory.Select(r => r.Spawn.Id).ToHashSet();
        plan.Suggestions.AddRange(st.In.CurrentSpawns
            .Where(sp => sp.DespawnedUtc == null && !sp.Filtered && !held.Contains(sp.Id) && st.In.Tree.IsDescendantOrSelf(sp.ResourceTypeId, res))
            .Select(sp => new ShortageSuggestion(sp, SlotScore(sp.Stats, modifiers), CapPercent(sp, modifiers, st.In.Tree)))
            .OrderBy(x => plan.KeepsSpecificBack && st.SpecificValue(x.Spawn) != null)   // general-purpose spawns first where that matters
            .ThenByDescending(x => x.Value)
            .Take(5));
    }

    /// <summary>The average, over the schematic's modifiers, of the weighted stat score (0-1000); 0 when there are no modifiers.</summary>
    public static double SlotScore(IReadOnlyDictionary<Stat, int> stats, IReadOnlyList<ExperimentModifier> modifiers) =>
        modifiers.Count == 0 ? 0 : modifiers.Average(m => HarvestPlanner.Score(stats, m.Weights));

    static double? CapPercent(Spawn spawn, IReadOnlyList<ExperimentModifier> modifiers, ResourceTree tree)
    {
        if (modifiers.Count == 0) return null;
        var parts = modifiers.Select(m => HarvestPlanner.CapPercent(spawn, m.Weights, tree)).ToList();
        return parts.Any(p => p == null) ? null : parts.Average(p => p!.Value);
    }
}
