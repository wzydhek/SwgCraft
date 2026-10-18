using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Tests;

public sealed class BuildPlannerTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    static readonly ResourceTree Tree = new(new ResourceTreeDocument
    {
        Nodes =
        {
            new ResourceNode
            {
                Id = "metal", Name = "Metal",
                Children = new()
                {
                    new ResourceNode { Id = "steel", Name = "Steel", Ranges = new() { [Stat.OQ] = new() { Min = 1, Max = 1000 } } },
                    new ResourceNode { Id = "iron", Name = "Iron", Ranges = new() { [Stat.OQ] = new() { Min = 1, Max = 1000 } } },
                },
            },
        },
    });

    // ------------------------------------------------------------ builders

    static BuildItem I(BuildPlan plan, string id) => plan.Items.Single(i => i.Id == id);

    static Spawn Sp(long id, string type, int oq, bool generic = false, bool filtered = false, bool current = true) =>
        new(id, 118, "spawn" + id, type, new Dictionary<Stat, int> { [Stat.OQ] = oq },
            Now.AddDays(-3), Now.AddDays(-3), Now, current ? null : Now, "Naboo", Filtered: filtered, Generic: generic);

    static InventoryItem Res(long id, string type, int oq, int qty, double? price = null, bool generic = false, bool filtered = false) =>
        new(id, Sp(id, type, oq, generic, filtered, current: false), qty, null, Now, price);

    static ItemStack Item(long id, MemberKind kind, string itemId, int qty, string? serial = null) =>
        new(id, kind, itemId, serial, qty, null, Now);

    static Schematic Sch(string id, bool quality = true, params (string Res, int Amount)[] resources)
    {
        var s = new Schematic { Id = id, Name = id, Professions = { "ws" } };
        foreach (var (r, a) in resources) s.Resources.Add(new ResourceSlot { Resource = r, Amount = a });
        if (quality)
            s.Experiments.Add(new Experiment { Name = "Experimental Quality", Modifiers = { new ExperimentModifier { Name = "Quality", Weights = { [Stat.OQ] = 100 } } } });
        return s;
    }

    static ComponentSlot Slot(SlotKind kind, string target, int qty = 1, bool identical = false, bool optional = false) =>
        new() { Kind = kind, Target = target, Quantity = qty, Identical = identical, Optional = optional };

    static BuildInputs Inputs(IEnumerable<Schematic> schematics, IEnumerable<InventoryItem>? res = null, IEnumerable<ItemStack>? items = null,
                              IEnumerable<Spawn>? current = null, ComponentClassesDocument? classes = null, LootItemsDocument? loot = null,
                              Func<string, bool>? enabled = null)
    {
        var doc = new SchematicsDocument();
        doc.Schematics.AddRange(schematics);
        return new BuildInputs(new SchematicCatalog(doc), new ClassCatalog(classes ?? new()), new LootCatalog(loot ?? new()), Tree,
                               (res ?? Array.Empty<InventoryItem>()).ToList(), (items ?? Array.Empty<ItemStack>()).ToList(),
                               (current ?? Array.Empty<Spawn>()).ToList(), enabled ?? (_ => true));
    }

    // ------------------------------------------------------------ resources

    [Fact]
    public void Each_craft_takes_a_slot_from_one_spawn_and_the_best_spawn_is_used_first()
    {
        var rifle = Sch("rifle", true, ("steel", 10));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle }, new[] { Res(1, "steel", 900, 25), Res(2, "steel", 500, 100) }), "rifle", 3);

        var slot = Assert.Single(plan.Target.Resources);
        Assert.Equal(30, slot.Needed);
        Assert.Equal(2, slot.Takes.Count);
        Assert.Equal((1L, 20, 2), (slot.Takes[0].Stack.Id, slot.Takes[0].Quantity, slot.Takes[0].Crafts));   // 25 units only cover 2 crafts of 10
        Assert.Equal((2L, 10, 1), (slot.Takes[1].Stack.Id, slot.Takes[1].Quantity, slot.Takes[1].Crafts));
        Assert.Equal(0, slot.ShortCrafts);
        Assert.True(plan.Complete);
    }

    [Fact]
    public void Leftovers_too_small_for_a_craft_are_a_shortage_and_current_spawns_are_suggested()
    {
        var rifle = Sch("rifle", true, ("steel", 10));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle },
            new[] { Res(1, "steel", 900, 15), Res(2, "steel", 800, 5) },
            current: new[] { Sp(10, "steel", 700), Sp(11, "steel", 950), Sp(12, "iron", 999), Sp(13, "steel", 990, filtered: true) }), "rifle", 2);

        var slot = Assert.Single(plan.Target.Resources);
        Assert.Equal(1, slot.ShortCrafts);
        Assert.Equal(10, slot.ShortAmount);
        Assert.Equal(new long[] { 11, 10 }, slot.Suggestions.Select(s => s.Spawn.Id).ToArray());   // only current steel, best first
        Assert.False(plan.Complete);
    }

    [Fact]
    public void Where_quality_does_not_matter_generic_spawns_come_first_then_the_cheapest()
    {
        var crate = Sch("crate", false, ("metal", 10));
        var plan = BuildPlanner.Plan(Inputs(new[] { crate }, new[]
        {
            Res(1, "steel", 999, 10, price: 1.0),
            Res(2, "iron", 100, 10, price: 5.0, generic: true),
            Res(3, "steel", 500, 10, price: 0.5),
            Res(4, "steel", 999, 50, price: 0.1, filtered: true),
        }), "crate", 3);
        Assert.Equal(new long[] { 2, 3, 1 }, plan.Target.Resources[0].Takes.Select(t => t.Stack.Id).ToArray());
        Assert.False(plan.Target.Resources[0].QualityMatters);
        Assert.Equal(65, plan.KnownCost, 6);
    }

    [Fact]
    public void Without_experimentation_the_biggest_general_purpose_stack_goes_first()
    {
        var crate = Sch("crate", false, ("metal", 10));
        var plan = BuildPlanner.Plan(Inputs(new[] { crate }, new[] { Res(1, "iron", 900, 50, price: 0.1), Res(2, "iron", 100, 200, price: 5) }), "crate", 1);
        Assert.Equal(2, plan.Target.Resources[0].Takes.Single().Stack.Id);
    }

    [Fact]
    public void An_any_slot_keeps_types_other_schematics_want_specifically_for_last()
    {
        // the blaster wants steel specifically (a bold line in What to Harvest); the frame takes any metal
        var blaster = Sch("blaster", true, ("steel", 10));
        var frame = Sch("frame", true, ("metal", 10));
        var plan = BuildPlanner.Plan(Inputs(new[] { frame, blaster }, new[] { Res(1, "steel", 990, 100), Res(2, "iron", 400, 15) }), "frame", 2);
        var takes = plan.Target.Resources[0].Takes;
        Assert.Equal(new[] { (2L, false), (1L, true) }, takes.Select(t => (t.Stack.Id, t.Specific)).ToArray());   // iron first, steel only when it runs out
        Assert.True(plan.Target.Resources[0].KeepsSpecificBack);

        // a slot that names steel itself just takes the best steel
        var direct = BuildPlanner.Plan(Inputs(new[] { frame, blaster }, new[] { Res(1, "steel", 990, 100), Res(3, "steel", 500, 100) }), "blaster", 1);
        Assert.False(direct.Target.Resources[0].KeepsSpecificBack);
        Assert.Equal(1, direct.Target.Resources[0].Takes.Single().Stack.Id);
    }

    [Fact]
    public void When_only_specific_types_are_held_the_least_valuable_one_is_used()
    {
        var blaster = Sch("blaster", true, ("steel", 10));
        var crate = Sch("crate", false, ("metal", 10));
        var plan = BuildPlanner.Plan(Inputs(new[] { crate, blaster }, new[] { Res(1, "steel", 990, 100), Res(2, "steel", 300, 100) }), "crate", 1);
        var take = plan.Target.Resources[0].Takes.Single();
        Assert.Equal((2L, true), (take.Stack.Id, take.Specific));

        // with the blaster's profession off, steel isn't wanted specifically any more: the biggest stack wins again
        var off = BuildPlanner.Plan(Inputs(new[] { crate, blaster }, new[] { Res(1, "steel", 990, 100), Res(2, "steel", 300, 50) },
                                           enabled: _ => false), "crate", 1);
        Assert.Equal((1L, false), (off.Target.Resources[0].Takes.Single().Stack.Id, off.Target.Resources[0].Takes.Single().Specific));
    }

    [Fact]
    public void The_build_toggle_and_the_schematic_flag_both_turn_quality_off()
    {
        var rifle = Sch("rifle", true, ("steel", 10));
        var stock = new[] { Res(1, "steel", 999, 10, price: 9), Res(2, "steel", 100, 10, price: 1) };
        Assert.Equal(1, BuildPlanner.Plan(Inputs(new[] { rifle }, stock), "rifle", 1).Target.Resources[0].Takes[0].Stack.Id);
        Assert.Equal(2, BuildPlanner.Plan(Inputs(new[] { rifle }, stock), "rifle", 1, new BuildOptions(IgnoreExperimentation: true))
                              .Target.Resources[0].Takes[0].Stack.Id);
        rifle.IgnoreExperimentation = true;
        Assert.Equal(2, BuildPlanner.Plan(Inputs(new[] { rifle }, stock), "rifle", 1).Target.Resources[0].Takes[0].Stack.Id);
    }

    [Fact]
    public void The_target_gets_the_best_stock_before_its_parts()
    {
        var part = Sch("part", true, ("steel", 10));
        var rifle = Sch("rifle", true, ("steel", 10));
        rifle.Components.Add(Slot(SlotKind.Schematic, "part"));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle, part }, new[] { Res(1, "steel", 990, 10), Res(2, "steel", 600, 10) }), "rifle", 1);
        Assert.Equal(1, plan.Target.Resources[0].Takes.Single().Stack.Id);
        Assert.Equal(2, I(plan, "part").Resources[0].Takes.Single().Stack.Id);
    }

    // ------------------------------------------------------------ components

    [Fact]
    public void Components_come_from_item_inventory_first_and_the_rest_is_crafted()
    {
        var handler = Sch("handler", true, ("steel", 5));
        var rifle = Sch("rifle", true);
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler", qty: 2));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle, handler }, new[] { Res(1, "steel", 800, 100) },
                                            new[] { Item(1, MemberKind.Schematic, "handler", 4) }), "rifle", 3);

        var child = I(plan, "handler");
        Assert.Equal((6, 4, 2), (child.Needed, child.FromInventoryCount, child.CraftCount));
        Assert.Equal(10, child.Resources[0].Needed);   // resources only for the 2 crafted
    }

    [Fact]
    public void The_same_component_in_several_slots_is_one_item_with_the_demand_added_up()
    {
        var wall = Sch("wall", false, ("steel", 10));
        var house = Sch("house", false);
        house.Components.Add(Slot(SlotKind.Schematic, "wall", qty: 1));
        house.Components.Add(Slot(SlotKind.Schematic, "wall", qty: 6));
        var plan = BuildPlanner.Plan(Inputs(new[] { house, wall }, new[] { Res(1, "steel", 500, 1000) },
                                            new[] { Item(1, MemberKind.Schematic, "wall", 3) }), "house", 1);

        Assert.Equal(2, plan.Items.Count);                       // house and wall, once each
        var w = I(plan, "wall");
        Assert.Equal((7, 3, 4), (w.Needed, w.FromInventoryCount, w.CraftCount));
        var comp = Assert.Single(plan.Target.Components);
        Assert.Equal((7, 2), (comp.PerUnit, comp.Slots));
        Assert.Equal(40, w.Resources[0].Needed);
    }

    [Fact]
    public void A_component_used_at_different_levels_is_added_up_across_the_build()
    {
        var handler = Sch("handler", false, ("steel", 1));
        var barrel = Sch("barrel", false);
        barrel.Components.Add(Slot(SlotKind.Schematic, "handler", qty: 2));
        var rifle = Sch("rifle", false);
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler"));
        rifle.Components.Add(Slot(SlotKind.Schematic, "barrel"));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle, barrel, handler }, new[] { Res(1, "steel", 500, 100) }), "rifle", 2);

        var h = I(plan, "handler");
        Assert.Equal(6, h.Needed);                                // 2 x 1 for the rifles + 2 x 2 for the barrels
        Assert.Equal(2, h.Level);                                 // deepest use
        Assert.Equal(new[] { "rifle", "barrel", "handler" }, plan.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void An_identical_slot_only_uses_whole_groups_from_one_serial_stack()
    {
        var handler = Sch("handler", false);
        var rifle = Sch("rifle", false);
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler", qty: 2, identical: true));
        var items = new[]
        {
            Item(1, MemberKind.Schematic, "handler", 3, "A"),    // one group of 2
            Item(2, MemberKind.Schematic, "handler", 10),        // no serial: can't vouch they're identical
            Item(3, MemberKind.Schematic, "handler", 2, "B"),    // one group of 2
        };
        var child = I(BuildPlanner.Plan(Inputs(new[] { rifle, handler }, items: items), "rifle", 3), "handler");
        Assert.Equal(4, child.FromInventoryCount);
        Assert.All(child.FromInventory, t => Assert.Equal(2, t.Quantity));
        Assert.Equal(2, child.CraftCount);
    }

    [Fact]
    public void Looted_items_cannot_be_crafted_so_a_shortage_is_missing()
    {
        var rifle = Sch("rifle", false);
        rifle.Components.Add(Slot(SlotKind.LootItem, "scope", qty: 1));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle }, items: new[] { Item(1, MemberKind.LootItem, "scope", 1) }), "rifle", 3);
        var child = I(plan, "scope");
        Assert.Equal(BuildItemKind.LootItem, child.ItemKind);
        Assert.Equal((1, 2), (child.FromInventoryCount, child.MissingCount));
        Assert.False(plan.Complete);
    }

    [Fact]
    public void A_class_slot_uses_any_member_held_then_crafts_a_member_of_an_enabled_profession()
    {
        var basic = Sch("basic_scope", false); basic.Professions[0] = "off";
        var adv = Sch("adv_scope", false);
        var classes = new ComponentClassesDocument();
        classes.Classes.Add(new ComponentClass
        {
            Id = "scope", Name = "Scope",
            Members = { new ClassMember { Kind = MemberKind.Schematic, Id = "basic_scope" }, new ClassMember { Kind = MemberKind.Schematic, Id = "adv_scope" },
                        new ClassMember { Kind = MemberKind.LootItem, Id = "loot_scope" } },
        });
        var rifle = Sch("rifle", false);
        rifle.Components.Add(Slot(SlotKind.Class, "scope"));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle, basic, adv }, items: new[] { Item(1, MemberKind.LootItem, "loot_scope", 1) },
                                            classes: classes, enabled: p => p != "off"), "rifle", 3);
        var cls = I(plan, "scope");
        Assert.Equal(1, cls.FromInventoryCount);
        Assert.Equal("adv_scope", cls.Recipe!.Id);
        Assert.Equal(2, cls.CraftCount);
        Assert.Equal(2, I(plan, "adv_scope").CraftCount);
    }

    [Fact]
    public void Force_craft_ignores_inventory_and_optional_slots_are_skipped_unless_asked()
    {
        var handler = Sch("handler", false);
        var stock = Sch("stock", false);
        var rifle = Sch("rifle", false);
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler"));
        rifle.Components.Add(Slot(SlotKind.Schematic, "stock", optional: true));
        var inputs = Inputs(new[] { rifle, handler, stock }, items: new[] { Item(1, MemberKind.Schematic, "handler", 5) });

        var normal = BuildPlanner.Plan(inputs, "rifle", 1);
        Assert.Equal((1, 0), (I(normal, "handler").FromInventoryCount, I(normal, "handler").CraftCount));
        Assert.DoesNotContain(normal.Items, i => i.Id == "stock");
        Assert.Contains("stock", normal.Target.SkippedOptional);

        var forced = BuildPlanner.Plan(inputs, "rifle", 1, new BuildOptions(IncludeOptional: true,
                                       ForceCraft: new HashSet<string> { BuildPlanner.KeyFor(BuildItemKind.Schematic, "handler") }));
        Assert.Equal((0, 1), (I(forced, "handler").FromInventoryCount, I(forced, "handler").CraftCount));
        Assert.Equal(1, I(forced, "stock").CraftCount);
    }

    [Fact]
    public void Optional_slots_can_be_switched_one_by_one_and_a_class_slot_can_use_a_chosen_member()
    {
        var handler = Sch("handler", false);
        var stock = Sch("stock", false);
        var grip = Sch("grip", false);
        var basic = Sch("basic_scope", false);
        var adv = Sch("adv_scope", false);
        var classes = new ComponentClassesDocument();
        classes.Classes.Add(new ComponentClass
        {
            Id = "scope", Name = "Scope",
            Members = { new ClassMember { Kind = MemberKind.Schematic, Id = "basic_scope" }, new ClassMember { Kind = MemberKind.Schematic, Id = "adv_scope" },
                        new ClassMember { Kind = MemberKind.LootItem, Id = "loot_scope" } },
        });
        var rifle = Sch("rifle", false);
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler"));
        rifle.Components.Add(Slot(SlotKind.Schematic, "stock", optional: true));
        rifle.Components.Add(Slot(SlotKind.Class, "scope"));
        rifle.Components.Add(Slot(SlotKind.Schematic, "grip", optional: true));
        var inputs = Inputs(new[] { rifle, handler, stock, grip, basic, adv },
                            items: new[] { Item(1, MemberKind.LootItem, "loot_scope", 1) }, classes: classes);

        // defaults: no optional slots; the class takes the held looted scope, then crafts its default recipe
        var plan = BuildPlanner.Plan(inputs, "rifle", 2);
        Assert.Equal(4, plan.Target.SlotChoices.Count);
        Assert.Equal(new[] { true, false, true, false }, plan.Target.SlotChoices.Select(c => c.Included).ToArray());
        Assert.Equal(BuildPlanner.SlotKey("rifle", 1), plan.Target.SlotChoices[1].Key);
        Assert.Equal("scope", plan.Target.SlotChoices[2].ClassId);
        Assert.Equal((1, 1), (I(plan, "scope").FromInventoryCount, I(plan, "basic_scope").CraftCount));

        // switch just the stock on, and choose the advanced scope
        var chosen = BuildPlanner.Plan(inputs, "rifle", 2, new BuildOptions(
            OptionalToggle: new HashSet<string> { BuildPlanner.SlotKey("rifle", 1) },
            ClassChoice: new Dictionary<string, string> { [BuildPlanner.SlotKey("rifle", 2)] = BuildPlanner.MemberKey(MemberKind.Schematic, "adv_scope") }));
        Assert.Equal(2, I(chosen, "stock").CraftCount);
        Assert.DoesNotContain(chosen.Items, i => i.Id == "grip");
        Assert.Equal(2, I(chosen, "adv_scope").CraftCount);                  // the held looted scope isn't an advanced scope
        Assert.DoesNotContain(chosen.Items, i => i.Id == "scope" || i.Id == "basic_scope");
        Assert.Equal("schematic:adv_scope", chosen.Target.SlotChoices[2].ChosenMember);

        // all optional slots on, except the grip; the looted scope chosen: one held, one missing
        var loot = BuildPlanner.Plan(inputs, "rifle", 2, new BuildOptions(IncludeOptional: true,
            OptionalToggle: new HashSet<string> { BuildPlanner.SlotKey("rifle", 3) },
            ClassChoice: new Dictionary<string, string> { [BuildPlanner.SlotKey("rifle", 2)] = "loot:loot_scope" }));
        Assert.Contains(loot.Items, i => i.Id == "stock");
        Assert.DoesNotContain(loot.Items, i => i.Id == "grip");
        Assert.Equal((1, 1), (I(loot, "loot_scope").FromInventoryCount, I(loot, "loot_scope").MissingCount));

        // a choice that isn't a member of the class is ignored
        var bad = BuildPlanner.Plan(inputs, "rifle", 2, new BuildOptions(
            ClassChoice: new Dictionary<string, string> { [BuildPlanner.SlotKey("rifle", 2)] = "schematic:handler" }));
        Assert.Null(bad.Target.SlotChoices[2].ChosenMember);
        Assert.Equal(1, I(bad, "basic_scope").CraftCount);
    }

    // ------------------------------------------------------------ factory runs

    [Fact]
    public void A_factory_run_takes_each_slot_from_one_spawn_for_the_whole_run()
    {
        var rifle = Sch("rifle", true, ("steel", 10));
        var stock = new[] { Res(1, "steel", 900, 25), Res(2, "steel", 500, 100) };

        var byHand = BuildPlanner.Plan(Inputs(new[] { rifle }, stock), "rifle", 3);
        Assert.False(byHand.Target.FactoryRun);
        Assert.Equal(2, byHand.Target.Resources[0].Takes.Count);   // best spawn for 2 crafts, the next for 1

        var factory = BuildPlanner.Plan(Inputs(new[] { rifle }, stock), "rifle", 3, new BuildOptions(FactoryRunsByDefault: true));
        Assert.True(factory.Target.FactoryRun);
        Assert.Equal(new[] { 3 }, factory.Target.Runs.Select(r => r.Size));
        var take = Assert.Single(factory.Target.Resources[0].Takes);   // only spawn 2 covers 30 + 10 for the manufacturing schematic
        Assert.Equal((2L, 40, 3), (take.Stack.Id, take.Quantity, take.Crafts));
        Assert.Equal(40, factory.Target.Resources[0].Needed);
    }

    [Fact]
    public void Runs_are_split_when_no_spawn_covers_everything()
    {
        var rifle = Sch("rifle", true, ("steel", 10));
        // 35 covers a run of 2 (+1 for its manufacturing schematic), then 30 covers the other run of 2 (+1)
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle }, new[] { Res(1, "steel", 900, 35), Res(2, "steel", 500, 30) }), "rifle", 4,
                                     new BuildOptions(FactoryToggle: new HashSet<string> { "schematic:rifle" }));   // toggled on for the target only
        Assert.Equal(new[] { 2, 2 }, plan.Target.Runs.Select(r => r.Size));
        Assert.Equal(new long[] { 1, 2 }, plan.Target.Resources[0].Takes.Select(t => t.Stack.Id).ToArray());
        Assert.True(plan.Complete);
    }

    [Fact]
    public void A_lone_unit_left_over_is_hand_crafted_instead_of_a_factory_run_of_one()
    {
        var rifle = Sch("rifle", true, ("steel", 10));
        // 35 covers a run of 2 (+1); the last unit would be a run of 1 (20 units for 1 rifle), so it is crafted by hand (10)
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle }, new[] { Res(1, "steel", 900, 35), Res(2, "steel", 500, 15) }), "rifle", 3,
                                     new BuildOptions(FactoryRunsByDefault: true));
        var t = plan.Target;
        Assert.Equal(new[] { 2 }, t.Runs.Select(r => r.Size));
        Assert.Equal(1, t.HandUnits);
        Assert.Equal(0, t.UnmadeInRuns);
        Assert.Equal(new[] { (1L, 30), (2L, 10) }, t.Resources[0].Takes.Select(x => (x.Stack.Id, x.Quantity)).ToArray());
        Assert.True(plan.Complete);

        var steps = BuildSteps.From(plan, id => id, s => s.ItemId);
        Assert.Equal(new[] { true, false }, steps.Select(s => s.Factory).ToArray());
        Assert.Contains("by hand", steps[1].Title);
    }

    [Fact]
    public void Factory_runs_by_default_skip_items_only_one_is_needed_of()
    {
        // 1 mine (by hand: only one) <- 1 wall (by hand: only one) <- 10 structural modules (a factory run)
        var structural = Sch("structural", false, ("steel", 1));
        var wall = Sch("wall", false, ("steel", 1));
        wall.Components.Add(Slot(SlotKind.Schematic, "structural", qty: 10));
        var mine = Sch("mine", false, ("steel", 1));
        mine.Components.Add(Slot(SlotKind.Schematic, "wall"));
        var plan = BuildPlanner.Plan(Inputs(new[] { mine, wall, structural }, new[] { Res(1, "steel", 500, 1000) }), "mine", 1,
                                     new BuildOptions(FactoryRunsByDefault: true));
        Assert.False(plan.Target.FactoryRun);
        Assert.False(I(plan, "wall").FactoryRun);
        var s = I(plan, "structural");
        Assert.True(s.FactoryRun);
        Assert.Equal(10, s.Needed);                                       // the wall is hand-crafted, so no extra set
        Assert.Equal(new[] { 10 }, s.Runs.Select(r => r.Size));

        // 2 mines: a run of 2 needs an identical set of 3 walls (2 + 1 for its schematic), so the walls are a run of 3,
        // which needs one identical set of 40 structural modules (30 + 10 for the wall schematic)
        var two = BuildPlanner.Plan(Inputs(new[] { mine, wall, structural }, new[] { Res(1, "steel", 500, 1000) }), "mine", 2,
                                    new BuildOptions(FactoryRunsByDefault: true));
        Assert.True(two.Target.FactoryRun);
        Assert.Equal(3, I(two, "wall").Needed);
        Assert.True(I(two, "wall").FactoryRequired);
        Assert.Equal(40, I(two, "structural").Needed);
    }

    [Fact]
    public void Identical_components_are_always_factory_runs_and_groups_are_never_split()
    {
        var handler = Sch("handler", true, ("steel", 10));
        var rifle = Sch("rifle", false);
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler", qty: 2, identical: true));

        var ok = BuildPlanner.Plan(Inputs(new[] { rifle, handler }, new[] { Res(1, "steel", 900, 55), Res(2, "steel", 500, 30) }), "rifle", 3);
        var child = I(ok, "handler");
        Assert.True(child.FactoryRun);
        Assert.True(child.FactoryRequired);
        Assert.Equal(new[] { 4, 2 }, child.Runs.Select(r => r.Size));   // 55 units cover two groups of 2 (+1), 30 the last group (+1)
        Assert.Equal(new[] { 2, 2 }, child.Runs[0].Groups.Select(g => g.Units));
        Assert.False(ok.Target.FactoryRun);                               // the target itself is still hand-crafted

        // 35 units cover a run of 2 (+1); nothing left covers another pair
        var tight = BuildPlanner.Plan(Inputs(new[] { rifle, handler }, new[] { Res(1, "steel", 900, 35), Res(2, "steel", 500, 15) }), "rifle", 3);
        var c2 = I(tight, "handler");
        Assert.Equal(new[] { 2 }, c2.Runs.Select(r => r.Size));
        Assert.Equal(4, c2.Resources[0].ShortCrafts);
        Assert.Contains("identical groups of 2", c2.Problem);
        Assert.False(tight.Complete);
    }

    [Fact]
    public void Identical_groups_from_different_slots_share_a_run_when_the_stock_allows()
    {
        var module = Sch("module", false, ("steel", 10));
        var house = Sch("house", false);
        house.Components.Add(Slot(SlotKind.Schematic, "module", qty: 3, identical: true));
        house.Components.Add(Slot(SlotKind.Schematic, "module", qty: 2, identical: true));
        house.Components.Add(Slot(SlotKind.Schematic, "module", qty: 1));
        var plan = BuildPlanner.Plan(Inputs(new[] { house, module }, new[] { Res(1, "steel", 500, 70) }), "house", 1);

        var m = I(plan, "module");
        Assert.Equal(6, m.CraftCount);
        var run = Assert.Single(m.Runs);
        Assert.Equal(6, run.Size);
        Assert.Equal(new[] { 3, 2 }, run.Groups.Select(g => g.Units));
    }

    [Fact]
    public void Everything_below_a_factory_run_is_factory_made_unless_one_serial_stack_holds_the_whole_set()
    {
        var spring = Sch("spring", false, ("iron", 1));
        var handler = Sch("handler", false, ("steel", 1));
        handler.Components.Add(Slot(SlotKind.Schematic, "spring", qty: 2));
        var rifle = Sch("rifle", false, ("steel", 1));
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler"));
        var stock = new[] { Res(1, "steel", 500, 100), Res(2, "iron", 500, 100) };
        var loose = new[] { Item(1, MemberKind.Schematic, "handler", 1) };
        var schematics = new[] { rifle, handler, spring };

        var byHand = BuildPlanner.Plan(Inputs(schematics, stock, loose), "rifle", 3);
        Assert.False(I(byHand, "handler").FactoryRun);
        Assert.Equal((1, 2), (I(byHand, "handler").FromInventoryCount, I(byHand, "handler").CraftCount));

        var rifleRun = new BuildOptions(FactoryToggle: new HashSet<string> { "schematic:rifle" });
        var plan = BuildPlanner.Plan(Inputs(schematics, stock, loose), "rifle", 3, rifleRun);
        var h = I(plan, "handler");
        Assert.Equal(4, h.Needed);                                       // 3 for the run + 1 for the rifle's manufacturing schematic
        Assert.Equal((0, 4), (h.FromInventoryCount, h.CraftCount));      // a loose handler can't be part of an identical set
        Assert.True(h.FactoryRun && h.FactoryRequired);
        Assert.Contains("rifle", h.FactoryReason);
        var s = I(plan, "spring");
        Assert.Equal(10, s.CraftCount);                                  // 2 x (4 + 1)
        Assert.True(s.FactoryRun && s.FactoryRequired);                  // inherited all the way down

        // switching it off on an inherited item is ignored
        var still = BuildPlanner.Plan(Inputs(schematics, stock, loose), "rifle", 3,
                                      new BuildOptions(FactoryToggle: new HashSet<string> { "schematic:rifle", "schematic:spring" }));
        Assert.True(I(still, "spring").FactoryRun);

        // a serial stack holding the whole set is used instead
        var serial = BuildPlanner.Plan(Inputs(schematics, stock, new[] { Item(2, MemberKind.Schematic, "handler", 4, "H") }), "rifle", 3, rifleRun);
        Assert.Equal((4, 0), (I(serial, "handler").FromInventoryCount, I(serial, "handler").CraftCount));
        Assert.DoesNotContain(serial.Items, i => i.Id == "spring");
    }

    [Fact]
    public void A_factory_run_turns_similar_slots_into_identical_sets_including_one_for_the_manufacturing_schematic()
    {
        // 7 wall modules (slots of 1 and 6) as a factory run; each wall module needs 10 structural modules
        var structural = Sch("structural", false, ("steel", 1));
        var wall = Sch("wall", false, ("steel", 1));
        wall.Components.Add(Slot(SlotKind.Schematic, "structural", qty: 10));
        var mine = Sch("mine", false, ("steel", 1));
        mine.Components.Add(Slot(SlotKind.Schematic, "wall", qty: 1));
        mine.Components.Add(Slot(SlotKind.Schematic, "wall", qty: 6));
        var plan = BuildPlanner.Plan(Inputs(new[] { mine, wall, structural }, new[] { Res(1, "steel", 500, 10_000) },
                                            new[] { Item(1, MemberKind.Schematic, "structural", 1) }), "mine", 1,
                                     new BuildOptions(FactoryToggle: new HashSet<string> { "schematic:wall" }));

        var w = I(plan, "wall");
        Assert.Equal(new[] { 7 }, w.Runs.Select(r => r.Size));
        Assert.Equal(8, w.Resources[0].Takes.Single().Quantity);        // 7 + 1 for the manufacturing schematic
        var s = I(plan, "structural");
        Assert.Equal(80, s.Needed);                                      // 70 for the run + 10 for the manufacturing schematic
        Assert.Equal((0, 80), (s.FromInventoryCount, s.CraftCount));     // the loose one on hand can't join the identical set
        Assert.Equal(new[] { 80 }, s.Runs.Select(r => r.Size));
        Assert.True(plan.Complete);

        var steps = BuildSteps.From(plan, id => id, st => st.ItemId);
        Assert.Equal(new[] { "structural", "wall", "mine" }, steps.Select(x => x.Item.Id).ToArray());
        Assert.Contains(steps[1].Lines, l => l.StartsWith("Component structural") &&
                                             l.Contains("70 for the run + 10 for the manufacturing schematic = 80") && l.Contains("from step 1"));
        Assert.Contains(steps[1].Lines, l => l.Contains("8 units (7 for the run + 1 for the manufacturing schematic)"));
        Assert.Contains(steps[0].Lines, l => l.Contains("80 for wall"));
    }

    [Fact]
    public void Inventory_covering_a_factory_items_parts_means_nothing_below_is_made()
    {
        var spring = Sch("spring", false, ("iron", 1));
        var handler = Sch("handler", false, ("steel", 1));
        handler.Components.Add(Slot(SlotKind.Schematic, "spring"));
        var rifle = Sch("rifle", false, ("steel", 1));
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler"));
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle, handler, spring }, new[] { Res(1, "steel", 500, 100) },
                                            new[] { Item(1, MemberKind.Schematic, "handler", 5, "H") }), "rifle", 3,
                                     new BuildOptions(FactoryRunsByDefault: true));
        Assert.Equal((4, 0), (I(plan, "handler").FromInventoryCount, I(plan, "handler").CraftCount));   // 3 for the run + 1 for its schematic
        Assert.DoesNotContain(plan.Items, i => i.Id == "spring");
        Assert.True(plan.Complete);
    }

    // ------------------------------------------------------------ build steps

    [Fact]
    public void Build_steps_put_parts_first_with_one_step_per_factory_run_and_the_final_item_last()
    {
        var handler = Sch("handler", true, ("steel", 10));
        var barrel = Sch("barrel", false, ("iron", 5));
        var rifle = Sch("rifle", true, ("iron", 20));
        rifle.Components.Add(Slot(SlotKind.Schematic, "handler", qty: 2, identical: true));
        rifle.Components.Add(Slot(SlotKind.Schematic, "barrel"));
        // steel: 45 covers a run of 4 handlers, 25 the last 2; iron covers the rifles and the barrels
        var stock = new[] { Res(1, "steel", 900, 55), Res(2, "steel", 500, 30), Res(4, "iron", 300, 100) };
        var plan = BuildPlanner.Plan(Inputs(new[] { rifle, handler, barrel }, stock), "rifle", 3);

        var steps = BuildSteps.From(plan, id => id, s => s.ItemId);
        Assert.Equal(new[] { "handler", "handler", "barrel", "rifle" }, steps.Select(s => s.Item.Id).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, steps.Select(s => s.Number).ToArray());
        Assert.True(steps[0].Factory && steps[1].Factory);           // identical: factory runs
        Assert.Equal(new[] { 4, 2 }, new[] { steps[0].Units, steps[1].Units });
        Assert.False(steps[3].Factory);
        Assert.Contains("final item", steps[3].Title);
        Assert.Contains(steps[3].Lines, l => l.StartsWith("Component handler") && l.Contains("steps 1, 2"));
        Assert.Contains(steps[3].Lines, l => l.StartsWith("Component barrel") && l.Contains("step 3"));

        var text = BuildSteps.ToText(plan, steps, id => id, s => s.ItemId);
        Assert.Contains("GATHER FIRST", text);
        Assert.Contains("STEP 4. Craft 3 x rifle (the final item) by hand", text);
    }

    [Fact]
    public void Each_step_sums_up_the_resources_it_takes_per_spawn()
    {
        var tower = Sch("tower", false, ("steel", 50), ("iron", 100), ("iron", 100));
        var plan = BuildPlanner.Plan(Inputs(new[] { tower }, new[] { Res(1, "steel", 500, 10_000), Res(2, "iron", 500, 20_000) }), "tower", 80,
                                     new BuildOptions(FactoryRunsByDefault: true));
        var step = Assert.Single(BuildSteps.From(plan, id => id, s => s.ItemId));
        Assert.Equal(2, step.Lines.Count(l => l.Contains("spawn2 - 8,100 units (8,000 for the run + 100 for the manufacturing schematic)")));
        Assert.Contains(step.Lines, l => l.Contains("spawn1 - 4,050 units (4,000 for the run + 50 for the manufacturing schematic)"));
        int at = step.Lines.ToList().IndexOf("Resources for this step:");
        Assert.True(at >= 0);
        Assert.Contains("4,050  spawn1", step.Lines[at + 2]);    // sorted by resource name: iron, then steel
        Assert.Contains("16,200  spawn2", step.Lines[at + 1]);
    }

    [Fact]
    public void A_schematic_that_needs_itself_is_reported_not_looped()
    {
        var a = Sch("a", false);
        var b = Sch("b", false);
        a.Components.Add(Slot(SlotKind.Schematic, "b"));
        b.Components.Add(Slot(SlotKind.Schematic, "a"));
        var plan = BuildPlanner.Plan(Inputs(new[] { a, b }), "a", 1);
        var loop = plan.Items.Single(n => n.MissingCount > 0);
        Assert.Contains("itself", loop.Problem);
    }
}
