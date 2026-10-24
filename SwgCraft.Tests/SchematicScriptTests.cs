using SwgCraft.Core;

namespace SwgCraft.Tests;

public sealed class SchematicScriptTests
{
    const string Dxr6 = """
        --Copyright (C) 2010 <SWGEmu>
        object_draft_schematic_weapon_rifle_disrupter_dxr6 = object_draft_schematic_weapon_shared_rifle_disrupter_dxr6:new {
           templateType = DRAFTSCHEMATIC,
           customObjectName = "DXR-6 Carbine",
           craftingToolTab = 1, -- (See DraftSchematicObjectTemplate.h)
           complexity = 25,
           size = 1,
           ingredientTemplateNames = {"craft_weapon_ingredients_n", "craft_weapon_ingredients_n", "craft_weapon_ingredients_n", "craft_weapon_ingredients_n", "craft_weapon_ingredients_n", "craft_weapon_ingredients_n"},
           ingredientTitleNames = {"frame_assembly", "receiver_assembly", "grip_assembly", "powerhandler", "barrel", "scope"},
           ingredientSlotType = {0, 0, 0, 1, 1, 3},
           resourceTypes = {"aluminum_titanium", "copper_platinite", "metal", "object/tangible/component/weapon/shared_blaster_power_handler.iff", "object/tangible/component/weapon/shared_blaster_rifle_barrel.iff", "object/tangible/component/weapon/shared_scope_weapon.iff"},
           resourceQuantities = {65, 25, 10, 5, 1, 1},
           contribution = {100, 100, 100, 100, 100, 100},
           targetTemplate = "object/weapon/ranged/carbine/carbine_dxr6.iff",
        }
        """;

    static StringCatalog Strings()
    {
        var doc = new StringsDocument();
        var f = new StringFile { Name = "craft_weapon_ingredients_n" };
        foreach (var (id, text) in new[] { ("frame_assembly", "Weapon Frame Assembly"), ("receiver_assembly", "Blaster Assembly Receiver Housing"),
                                           ("powerhandler", "Blaster Power Handler Component."), ("barrel", "Ranged Weapon Barrel Component"),
                                           ("scope", "Ranged Weapon Scope Component") })
            f.Entries.Add(new StringEntry { Id = id, Value = text });
        doc.Files.Add(f);
        return new StringCatalog(doc);
    }

    [Fact]
    public void A_script_is_read_slot_by_slot()
    {
        var s = SchematicScriptReader.Parse(Dxr6);
        Assert.Null(s.Problem);
        Assert.Equal(("DXR-6 Carbine", 25, 1), (s.CustomObjectName, s.Complexity, s.Size));
        Assert.Equal(6, s.Slots.Count);
        Assert.Equal(new[] { true, true, true, false, false, false }, s.Slots.Select(x => x.IsResource).ToArray());
        var handler = s.Slots[3];
        Assert.Equal(("powerhandler", 5, true, false), (handler.TitleId, handler.Quantity, handler.Identical, handler.Optional));
        Assert.True(s.Slots[5].Optional);
        Assert.False(s.Slots[5].Identical);   // identical, but only one
    }

    [Fact]
    public void Merging_fills_blanks_and_adds_missing_slots_but_never_changes_what_is_entered()
    {
        var current = new Schematic { Id = "weapon_rifle_disrupter_dxr6", Name = "DXR6 Carbine", Complexity = 30 };
        current.Resources.Add(new ResourceSlot { Resource = "aluminum_titanium", Amount = 65 });
        current.Resources.Add(new ResourceSlot { Name = "My Receiver", Resource = "copper_platinite", Amount = 20 });
        current.Components.Add(new ComponentSlot { Kind = SlotKind.Class, Target = "handlers", Quantity = 5, Identical = true });

        var r = SchematicScriptMerge.Merge(current, SchematicScriptReader.Parse(Dxr6), Strings(),
                                           SchematicCatalog.Empty, ClassCatalog.Empty, tree: null, index: null);
        var m = r.Merged;

        Assert.Equal("Weapon Frame Assembly", m.Resources[0].Name);                  // blank: filled
        Assert.Equal("My Receiver", m.Resources[1].Name);                            // entered: kept
        Assert.Equal(20, m.Resources[1].Amount);                                     // entered: kept...
        Assert.Contains(r.Differences, d => d.Contains("amount: entered 20, script 25"));   // ...and reported
        Assert.Equal(("metal", 10), (m.Resources[2].Resource, m.Resources[2].Amount)); // missing: added
        Assert.Null(m.Resources[2].Name);                                            // no string for grip_assembly
        Assert.Contains(r.Notes, n => n.Contains("grip_assembly"));

        Assert.Equal("Blaster Power Handler Component", m.Components[0].Name);       // trailing period dropped
        Assert.Single(m.Components);                                                 // barrel and scope can't be worked out without the scripts folder
        Assert.Equal(2, r.Notes.Count(n => n.StartsWith("Component slot")));

        Assert.Equal(30, m.Complexity);                                              // entered: kept
        Assert.Equal(1, m.Size);                                                     // empty: filled
        Assert.Contains(r.Differences, d => d.StartsWith("Complexity"));
        Assert.Null(current.Resources[0].Name);                                      // the original is untouched
    }

    [Fact]
    public void Scripts_are_found_by_any_trailing_part_of_their_path_and_component_slots_by_what_makes_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "swgcraft-scripts-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "weapon", "component"));
            Directory.CreateDirectory(Path.Combine(root, "armor"));
            File.WriteAllText(Path.Combine(root, "weapon", "rifle_disrupter_dxr6.lua"), Dxr6);
            File.WriteAllText(Path.Combine(root, "armor", "armor_segment_zam.lua"), "targetTemplate = \"object/tangible/x.iff\"");
            File.WriteAllText(Path.Combine(root, "weapon", "component", "blaster_power_handler.lua"),
                              "targetTemplate = \"object/tangible/component/weapon/blaster_power_handler.iff\",");
            File.WriteAllText(Path.Combine(root, "weapon", "component", "blaster_rifle_barrel.lua"),
                              "targetTemplate = \"object/tangible/component/weapon/blaster_rifle_barrel.iff\",");
            File.WriteAllText(Path.Combine(root, "weapon", "objects.lua"), "-- not a schematic");

            var index = new SchematicScriptIndex(root);
            Assert.Equal(4, index.FileCount);
            Assert.NotNull(index.Find("weapon_rifle_disrupter_dxr6"));
            Assert.NotNull(index.Find("armor_segment_zam"));                     // armor/armor_segment_zam.lua
            Assert.NotNull(index.Find("armor_armor_segment_zam"));
            Assert.Null(index.Find("nothing_here"));

            var doc = new SchematicsDocument();
            foreach (var id in new[] { "weapon_rifle_disrupter_dxr6", "weapon_component_blaster_power_handler", "weapon_component_blaster_rifle_barrel" })
                doc.Schematics.Add(new Schematic { Id = id, Name = id });
            var classes = new ComponentClassesDocument();
            classes.Classes.Add(new ComponentClass { Id = "handlers", Name = "Handlers",
                Members = { new ClassMember { Kind = MemberKind.Schematic, Id = "weapon_component_blaster_power_handler" } } });

            var r = SchematicScriptMerge.Merge(new Schematic { Id = "weapon_rifle_disrupter_dxr6" }, SchematicScriptReader.ReadFile(index.Find("weapon_rifle_disrupter_dxr6")!),
                                               Strings(), new SchematicCatalog(doc), new ClassCatalog(classes), tree: null, index);
            var comps = r.Merged.Components;
            Assert.Equal(2, comps.Count);
            Assert.Equal((SlotKind.Class, "handlers", 5, true), (comps[0].Kind, comps[0].Target, comps[0].Quantity, comps[0].Identical));    // a class holds the maker
            Assert.Equal((SlotKind.Schematic, "weapon_component_blaster_rifle_barrel"), (comps[1].Kind, comps[1].Target));                   // the one maker
            Assert.Contains(r.Notes, n => n.Contains("scope_weapon"));                                                                        // nothing makes it here
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_slot_takes_its_template_and_everything_derived_from_it()
    {
        var objects = ObjectTemplateIndex.FromText("""
            object_tangible_component_weapon_shared_blaster_power_handler = SharedTangibleObjectTemplate:new {
                clientTemplateFileName = "object/tangible/component/weapon/shared_blaster_power_handler.iff"
            --[[
                objectName = "@craft_weapon_ingredients_n:blaster_power_handler",
                derivedFromTemplates = {"object/tangible/component/base/shared_base_component.iff"}
            ]]
            }
            ObjectTemplates:addClientTemplate(object_tangible_component_weapon_shared_blaster_power_handler, "x")
            object_tangible_component_weapon_shared_blaster_power_handler_quest = SharedTangibleObjectTemplate:new {
                clientTemplateFileName = "object/tangible/component/weapon/shared_blaster_power_handler_quest.iff"
            --[[
                objectName = "@craft_weapon_ingredients_n:powerhandler",
                derivedFromTemplates = {"object/tangible/component/base/shared_base_component.iff", "object/tangible/component/weapon/shared_blaster_power_handler.iff"}
            ]]
            }
            ObjectTemplates:addClientTemplate(object_tangible_component_weapon_shared_blaster_power_handler_quest, "y")
            object_tangible_component_weapon_shared_scope_weapon = SharedTangibleObjectTemplate:new {
                clientTemplateFileName = "object/tangible/component/weapon/shared_scope_weapon.iff"
            }
            ObjectTemplates:addClientTemplate(object_tangible_component_weapon_shared_scope_weapon, "z")
            """);

        Assert.Equal(3, objects.Count);
        Assert.Equal(new[] { "object/tangible/component/weapon/blaster_power_handler.iff", "object/tangible/component/weapon/blaster_power_handler_quest.iff" },
                     objects.Accepting("object/tangible/component/weapon/shared_blaster_power_handler.iff"));
        Assert.True(objects.IsBase("object/tangible/component/weapon/blaster_power_handler.iff"));
        Assert.False(objects.IsBase("object/tangible/component/weapon/blaster_power_handler_quest.iff"));
        Assert.Equal("Blaster Power Handler Component.", objects.NameOf("object/tangible/component/weapon/shared_blaster_power_handler_quest.iff", Strings()));
        Assert.Single(objects.Accepting("object/tangible/component/weapon/shared_scope_weapon.iff"));
    }

    [Fact]
    public void Loot_items_and_the_draft_schematics_loot_schematics_teach_are_indexed()
    {
        var ix = LootScriptIndex.FromText(
            new[] { ("blaster_power_handler_quest", """
                blaster_power_handler_quest = {
                    directObjectTemplate = "object/tangible/component/weapon/blaster_power_handler_quest.iff",
                }
                """) },
            new[] { """
                object_tangible_loot_loot_schematic_agitator_motor_schematic = object_tangible_loot_loot_schematic_shared_agitator_motor_schematic:new {
                    templateType = LOOTSCHEMATIC,
                    targetDraftSchematic = "object/draft_schematic/item/item_agitator_motor.iff",
                }
                ObjectTemplates:addTemplate(object_tangible_loot_loot_schematic_agitator_motor_schematic, "object/tangible/loot/loot_schematic/agitator_motor_schematic.iff")
                """ });

        Assert.Equal(new[] { "blaster_power_handler_quest" }, ix.LootItemsFor("object/tangible/component/weapon/shared_blaster_power_handler_quest.iff"));
        Assert.Empty(ix.LootItemsFor("object/tangible/component/weapon/blaster_power_handler.iff"));
        Assert.Equal(new[] { "object/tangible/loot/loot_schematic/agitator_motor_schematic.iff" }, ix.TeachersOf("object/draft_schematic/item/item_agitator_motor.iff"));

        var root = Path.Combine(Path.GetTempPath(), "swgcraft-ds-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "item"));
            var file = Path.Combine(root, "item", "item_agitator_motor.lua");
            File.WriteAllText(file, "targetTemplate = \"object/tangible/component/item/agitator_motor.iff\",");
            var scripts = new SchematicScriptIndex(root);
            Assert.Equal("object/draft_schematic/item/item_agitator_motor.iff", scripts.DraftTemplateOf(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Experimentation_is_read_from_the_object_script_and_compared_with_what_is_entered()
    {
        const string carbine = """
            object_weapon_ranged_carbine_carbine_dxr6 = object_weapon_ranged_carbine_shared_carbine_dxr6:new {
                numberExperimentalProperties = {1, 2, 2, 3, 1, 2},
                experimentalProperties = {"XX", "CD", "OQ", "CD", "OQ", "DR", "OQ", "UT", "XX", "MA", "OQ"},
                experimentalWeights = {1, 1, 1, 1, 2, 4, 1, 1, 1, 1, 1},
                experimentalGroupTitles = {"null", "expDamage", "expDamage", "exp_durability", "null", "exp_durability"},
                experimentalSubGroupTitles = {"null", "mindamage", "maxdamage", "hitpoints", "zerorangemod", "armor_action_encumbrance"},
            }
            """;
        var doc = new StringsDocument();
        var crafting = new StringFile { Name = "crafting" };
        crafting.Entries.Add(new StringEntry { Id = "expdamage", Value = "Experimental Damage " });   // case and spaces don't matter
        crafting.Entries.Add(new StringEntry { Id = "exp_durability", Value = "Experimental Durability" });
        var attr = new StringFile { Name = "obj_attr_n" };
        attr.Entries.Add(new StringEntry { Id = "mindamage", Value = "Minimum Damage" });
        attr.Entries.Add(new StringEntry { Id = "maxdamage", Value = "Maximum Damage" });
        attr.Entries.Add(new StringEntry { Id = "hitpoints", Value = "Unit Integrity" });
        doc.Files.Add(crafting);
        doc.Files.Add(attr);
        var mods = ExperimentScriptReader.Parse(carbine, new StringCatalog(doc))!;

        Assert.Equal(4, mods.Count);                                                       // the "null" group is not experimentable
        Assert.Equal(("Experimental Damage", "Minimum Damage"), (mods[0].ExperimentName, mods[0].ModifierName));
        Assert.Equal(new SortedDictionary<Stat, int> { [Stat.CD] = 33, [Stat.OQ] = 67 }, mods[1].Weights);   // weights 1:2
        Assert.Equal(new SortedDictionary<Stat, int> { [Stat.DR] = 67, [Stat.OQ] = 17, [Stat.UT] = 16 }, mods[2].Weights);
        Assert.Equal(new SortedDictionary<Stat, int> { [Stat.MB] = 50, [Stat.OQ] = 50 }, mods[3].Weights);  // MA is malleability
        Assert.Equal("armor_action_encumbrance", mods[3].ModifierName);                     // no string: the ID

        ExperimentModifier M(string name, params (Stat S, int P)[] w)
        {
            var m = new ExperimentModifier { Name = name };
            foreach (var (st, p) in w) m.Weights[st] = p;
            return m;
        }
        var s = new Schematic { Id = "dxr6" };
        s.Experiments.Add(new Experiment { Name = "Experimental Damage", Modifiers = { M("minimum damage", (Stat.CD, 50), (Stat.OQ, 50)), M("Maximum Damage", (Stat.CD, 50), (Stat.OQ, 50)) } });
        s.Experiments.Add(new Experiment { Name = "Experimental Durabiity", Modifiers = { M("Unit Integrity", (Stat.DR, 67), (Stat.OQ, 17), (Stat.UT, 16)), M("Armor Action Enc", (Stat.MB, 50), (Stat.OQ, 50)) } });
        s.Experiments.Add(new Experiment { Name = "Experimental Range", Modifiers = { M("Range", (Stat.OQ, 100)) } });

        var lines = ExperimentComparison.Compare(s, mods);
        Assert.Contains(lines, l => l == "Experimental name: entered \"Experimental Durabiity\", script \"Experimental Durability\"" || l.Contains("Durabiity") && l.Contains("name"));
        Assert.Contains(lines, l => l.StartsWith("Experimental Damage / Maximum Damage: entered CD 50, OQ 50, script CD 33, OQ 67"));
        Assert.DoesNotContain(lines, l => l.Contains("Minimum Damage") || l.Contains("minimum damage"));   // same, ignoring case
        Assert.Contains(lines, l => l.Contains("modifier name: entered \"Armor Action Enc\", script \"armor_action_encumbrance\""));
        Assert.Contains(lines, l => l == "Experimental \"Experimental Range\" is entered but not in the script");
    }

    [Fact]
    public void An_attribute_with_a_fixed_value_is_not_experimentable()
    {
        const string spice = """
            object_tangible_food_spice_spice_booster_blue = object_tangible_food_spice_shared_spice_booster_blue:new {
                numberExperimentalProperties = {1, 1, 3},
                experimentalProperties = {"XX", "XX", "DR", "OQ", "UT"},
                experimentalWeights = {1, 1, 4, 1, 1},
                experimentalGroupTitles = {"null", "null", "exp_nutrition"},
                experimentalSubGroupTitles = {"null", "null", "hitpoints"},
                experimentalMin = {0, 0, 1000},
                experimentalMax = {0, 0, 1000},
            }
            """;
        var mods = ExperimentScriptReader.Parse(spice, StringCatalog.Empty)!;
        Assert.Empty(mods);
        Assert.Empty(ExperimentComparison.Compare(new Schematic { Id = "spices_spice_booster_blue" }, mods));
        Assert.Single(ExperimentScriptReader.Parse(spice.Replace("experimentalMax = {0, 0, 1000}", "experimentalMax = {0, 0, 1200}"), StringCatalog.Empty)!);
    }

    [Fact]
    public void A_script_name_missing_from_the_string_tables_is_not_a_difference()
    {
        const string bio = """
            object_tangible_component_bio_bio_component_clothing_casual_medic_2 = x:new {
                numberExperimentalProperties = {2, 2},
                experimentalProperties = {"OQ", "PE", "OQ", "PE"},
                experimentalWeights = {1, 1, 1, 1},
                experimentalGroupTitles = {"exp_effectiveness", "exp_effectiveness"},
                experimentalSubGroupTitles = {"cat_skill_mod_bonus.@stat_n:healing_injury_treatment", "cat_skill_mod_bonus.@stat_n:healing_wound_treatment"},
                experimentalMin = {1, 1},
                experimentalMax = {25, 25},
            }
            """;
        var doc = new StringsDocument();
        var crafting = new StringFile { Name = "crafting" };
        crafting.Entries.Add(new StringEntry { Id = "exp_effectiveness", Value = "Experimental Effectiveness" });
        doc.Files.Add(crafting);
        var mods = ExperimentScriptReader.Parse(bio, new StringCatalog(doc))!;
        Assert.Equal(2, mods.Count);
        Assert.All(mods, m => Assert.False(m.ModifierNameKnown));

        var s = new Schematic { Id = "bio_component_clothing_casual_medic_2" };
        var exp = new Experiment { Name = "Experimental Effectiveness" };
        foreach (var name in new[] { "Mod One", "Mod Two" })
            exp.Modifiers.Add(new ExperimentModifier { Name = name, Weights = { [Stat.OQ] = 50, [Stat.PE] = 50 } });
        s.Experiments.Add(exp);
        Assert.Empty(ExperimentComparison.Compare(s, mods));

        exp.Modifiers[1].Weights[Stat.OQ] = 67;
        exp.Modifiers[1].Weights[Stat.PE] = 33;
        var lines = ExperimentComparison.Compare(s, mods);
        Assert.Contains(lines, l => l.StartsWith("Experimental Effectiveness / Mod Two: entered PE 33, OQ 67, script PE 50, OQ 50"));   // stats still compared
    }
}
