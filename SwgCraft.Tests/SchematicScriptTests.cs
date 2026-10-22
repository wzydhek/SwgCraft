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
}
