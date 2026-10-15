using SwgCraft.Core;

namespace SwgCraft.Tests;

public sealed class ComponentTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------ builders

    static bool Has(IEnumerable<ValidationIssue> issues, string code, Severity severity) =>
        issues.Any(i => i.Code == code && i.Severity == severity);

    static ProfessionsDocument Professions(params string[] ids)
    {
        var d = new ProfessionsDocument();
        foreach (var id in ids) d.Professions.Add(new Profession { Id = id, Name = id });
        return d;
    }

    static CategoriesDocument Categories(params (string Id, string Name)[] items)
    {
        var d = new CategoriesDocument();
        foreach (var (id, name) in items) d.Categories.Add(new CraftCategory { Id = id, Name = name });
        return d;
    }

    static ResourceTreeDocument Tree()
    {
        var d = new ResourceTreeDocument();
        d.Nodes.Add(new ResourceNode
        {
            Id = "metal", Name = "Metal",
            Children = new List<ResourceNode>
            {
                new() { Id = "iron", Name = "Iron" },
                new() { Id = "steel", Name = "Steel" },
            }
        });
        return d;
    }

    static ComponentClassesDocument Classes(params ComponentClass[] classes)
    {
        var d = new ComponentClassesDocument();
        d.Classes.AddRange(classes);
        return d;
    }

    static ComponentClass Class(string id, params (MemberKind Kind, string Id)[] members)
    {
        var c = new ComponentClass { Id = id, Name = id };
        foreach (var (k, i) in members) c.Members.Add(new ClassMember { Kind = k, Id = i });
        return c;
    }

    static Schematic Sch(string id, params string[] professions)
    {
        var s = new Schematic { Id = id, Name = id };
        s.Professions.AddRange(professions);
        return s;
    }

    static ComponentSlot Slot(SlotKind kind, string target, int qty = 1, bool identical = false, bool optional = false) =>
        new() { Kind = kind, Target = target, Quantity = qty, Identical = identical, Optional = optional };

    static ExperimentModifier Mod(string name, params (Stat Stat, int Pct)[] weights)
    {
        var m = new ExperimentModifier { Name = name };
        foreach (var (stat, pct) in weights) m.Weights[stat] = pct;
        return m;
    }

    /// <summary>An experimental with a single modifier of the same name (the common case).</summary>
    static Experiment Exp(string name, params (Stat Stat, int Pct)[] weights) => ExpOf(name, Mod(name, weights));

    static Experiment ExpOf(string name, params ExperimentModifier[] modifiers)
    {
        var e = new Experiment { Name = name };
        e.Modifiers.AddRange(modifiers);
        return e;
    }

    static List<ValidationIssue> Check(SchematicsDocument doc, ComponentClassesDocument? classes = null) =>
        SchematicValidator.Validate(doc, new ProfessionCatalog(Professions("weaponsmith", "armorsmith")),
                                    classes ?? new ComponentClassesDocument(), new ResourceTree(Tree()));

    static SchematicsDocument Doc(params Schematic[] items)
    {
        var d = new SchematicsDocument();
        d.Schematics.AddRange(items);
        return d;
    }

    /// <summary>The Beam Rifle row from the JnF sheet, expressed in the new model.</summary>
    static Schematic BeamRifle()
    {
        var s = Sch("beam_rifle", "weaponsmith");
        s.Name = "Beam Rifle";
        s.Category = "rifles";
        s.ShowOverride = false;
        s.Components.Add(Slot(SlotKind.Schematic, "blaster_power_handler", 4));
        s.Components.Add(Slot(SlotKind.Schematic, "blaster_rifle_barrel", 1));
        s.Components.Add(Slot(SlotKind.Class, "scope", optional: true));
        s.Components.Add(Slot(SlotKind.Class, "stock", optional: true));
        s.Resources.Add(new ResourceSlot { Resource = "iron", Amount = 60 });
        s.Resources.Add(new ResourceSlot { Resource = "metal", Amount = 15 });
        s.Resources.Add(new ResourceSlot { Resource = "steel", Amount = 120 });
        s.Experiments.Add(Exp("Experimental Damage / Durability / Efficiency / Range", (Stat.CD, 50), (Stat.OQ, 50)));
        return s;
    }

    static (SchematicsDocument, ComponentClassesDocument) BeamRifleWorld()
    {
        var doc = Doc(BeamRifle(), Sch("blaster_power_handler", "weaponsmith"), Sch("blaster_rifle_barrel", "weaponsmith"),
                      Sch("weapon_scope", "weaponsmith"), Sch("basic_stock", "weaponsmith"));
        var classes = Classes(Class("scope", (MemberKind.Schematic, "weapon_scope")), Class("stock", (MemberKind.Schematic, "basic_stock")));
        return (doc, classes);
    }

    // ------------------------------------------------------------ looted items

    static LootItemsDocument Loot(params (string Id, string Name)[] items)
    {
        var d = new LootItemsDocument();
        foreach (var (id, name) in items) d.Items.Add(new LootItem { Id = id, Name = name });
        return d;
    }

    [Fact]
    public void Loot_list_rules()
    {
        Assert.Empty(LootItemValidator.Validate(Loot(("precision_scope", "Precision Tuned Weapon Scope"))));
        Assert.True(Has(LootItemValidator.Validate(Loot(("a", "X"), ("a", "Y"))), "id.duplicate", Severity.Error));
        Assert.True(Has(LootItemValidator.Validate(Loot(("a", "X"), ("b", "x"))), "name.duplicate", Severity.Error));
        Assert.True(Has(LootItemValidator.Validate(Loot(("", "X"))), "id.empty", Severity.Error));
        Assert.True(Has(LootItemValidator.Validate(Loot(("a", " "))), "name.empty", Severity.Error));
        Assert.True(Has(LootItemValidator.Validate(Loot()), "list.empty", Severity.Warning));
    }

    // ------------------------------------------------------------ classes

    [Fact]
    public void Class_members_must_exist()
    {
        var classes = Classes(Class("vibro", (MemberKind.Schematic, "blade_vibro"), (MemberKind.LootItem, "light_blade")));
        var schematics = Doc(Sch("blade_vibro", "weaponsmith"));

        Assert.Empty(ClassValidator.Validate(classes, schematics, Loot(("light_blade", "Light Blade"))));
        Assert.True(Has(ClassValidator.Validate(classes, schematics, Loot()), "member.loot.unknown", Severity.Error));
        Assert.True(Has(ClassValidator.Validate(classes, Doc(), Loot(("light_blade", "Light Blade"))), "member.schematic.unknown", Severity.Error));
    }

    [Fact]
    public void Class_member_checks_are_skipped_when_a_list_is_not_given()
    {
        var classes = Classes(Class("vibro", (MemberKind.Schematic, "blade_vibro"), (MemberKind.LootItem, "light_blade")));
        Assert.DoesNotContain(ClassValidator.Validate(classes, null, null), i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Class_basic_rules()
    {
        Assert.True(Has(ClassValidator.Validate(Classes(Class("a"), Class("a")), null, null), "id.duplicate", Severity.Error));
        Assert.True(Has(ClassValidator.Validate(Classes(Class("a")), null, null), "members.empty", Severity.Warning));
        Assert.True(Has(ClassValidator.Validate(Classes(Class("a", (MemberKind.Schematic, ""))), null, null), "member.empty", Severity.Error));
        Assert.True(Has(ClassValidator.Validate(Classes(Class("a", (MemberKind.Schematic, "x"), (MemberKind.Schematic, "x"))), null, null),
                        "member.duplicate", Severity.Warning));
    }

    [Fact]
    public void The_same_id_as_a_schematic_and_as_a_loot_item_is_not_a_duplicate()
    {
        var classes = Classes(Class("c", (MemberKind.Schematic, "x"), (MemberKind.LootItem, "x")));
        var issues = ClassValidator.Validate(classes, Doc(Sch("x", "weaponsmith")), Loot(("x", "X")));
        Assert.DoesNotContain(issues, i => i.Code == "member.duplicate");
    }

    // ------------------------------------------------------------ schematics

    [Fact]
    public void The_beam_rifle_sample_is_valid()
    {
        var (doc, classes) = BeamRifleWorld();
        Assert.DoesNotContain(Check(doc, classes), i => i.Severity == Severity.Error);
    }

    [Fact]
    public void A_schematic_needs_at_least_one_known_profession()
    {
        Assert.True(Has(Check(Doc(Sch("a"))), "prof.none", Severity.Error));
        Assert.True(Has(Check(Doc(Sch("a", "jedi"))), "prof.unknown", Severity.Error));
        Assert.DoesNotContain(Check(Doc(Sch("a", "weaponsmith", "armorsmith"))), i => i.Severity == Severity.Error);   // two makers is fine
        Assert.True(Has(Check(Doc(Sch("a", "weaponsmith", "weaponsmith"))), "prof.duplicate", Severity.Warning));
    }

    [Fact]
    public void Identical_only_applies_to_quantity_two_or_more()
    {
        var s = Sch("a", "weaponsmith");
        s.Components.Add(Slot(SlotKind.Schematic, "a2", 1, identical: true));
        var doc = Doc(s, Sch("a2", "weaponsmith"));
        Assert.True(Has(Check(doc), "slot.identical.quantity", Severity.Error));

        s.Components[0].Quantity = 2;
        Assert.DoesNotContain(Check(doc), i => i.Code == "slot.identical.quantity");
    }

    [Fact]
    public void Slot_targets_must_exist_and_quantities_be_positive()
    {
        var s = Sch("a", "weaponsmith");
        s.Components.Add(Slot(SlotKind.Schematic, "missing"));
        s.Components.Add(Slot(SlotKind.Class, "missing_class"));
        s.Components.Add(Slot(SlotKind.Class, ""));
        s.Components.Add(Slot(SlotKind.Schematic, "a", 0));
        var issues = Check(Doc(s));
        Assert.True(Has(issues, "slot.schematic.unknown", Severity.Error));
        Assert.True(Has(issues, "slot.class.unknown", Severity.Error));
        Assert.True(Has(issues, "slot.empty", Severity.Error));
        Assert.True(Has(issues, "slot.quantity", Severity.Error));
    }

    [Fact]
    public void Resource_slots_may_point_at_groups_but_must_exist()
    {
        var s = Sch("a", "weaponsmith");
        s.Resources.Add(new ResourceSlot { Resource = "metal", Amount = 10 });      // interior node: "any metal"
        Assert.DoesNotContain(Check(Doc(s)), i => i.Severity == Severity.Error);

        s.Resources.Add(new ResourceSlot { Resource = "unobtainium", Amount = 10 });
        s.Resources.Add(new ResourceSlot { Resource = "iron", Amount = 0 });
        s.Resources.Add(new ResourceSlot { Resource = "iron", Amount = 5 });
        var issues = Check(Doc(s));
        Assert.True(Has(issues, "res.unknown", Severity.Error));
        Assert.True(Has(issues, "res.amount", Severity.Error));
        Assert.DoesNotContain(issues, i => i.Code == "res.duplicate");   // the same resource in two slots is allowed (two spawns can fill it)
    }

    [Fact]
    public void Experiments_must_total_exactly_100()
    {
        var s = Sch("a", "weaponsmith");
        s.Experiments.Add(Exp("fifty fifty", (Stat.CD, 50), (Stat.OQ, 50)));
        s.Experiments.Add(Exp("thirds", (Stat.ER, 67), (Stat.UT, 33)));
        s.Experiments.Add(Exp("all in", (Stat.PE, 100)));
        Assert.DoesNotContain(Check(Doc(s)), i => i.Severity == Severity.Error);

        s.Experiments.Add(Exp("short", (Stat.CD, 50), (Stat.OQ, 49)));
        s.Experiments.Add(Exp("long", (Stat.CD, 60), (Stat.OQ, 50)));
        s.Experiments.Add(Exp("empty"));
        var issues = Check(Doc(s)).Where(i => i.Code == "mod.sum").ToList();
        Assert.Equal(3, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("99") && i.Message.Contains("short"));
        Assert.Contains(issues, i => i.Message.Contains("110"));
        Assert.Contains(issues, i => i.Message.Contains("add up to 0"));
    }

    [Fact]
    public void Experiment_weights_must_be_between_1_and_100_and_lines_need_names()
    {
        var s = Sch("a", "weaponsmith");
        s.Experiments.Add(Exp("neg", (Stat.CD, 150), (Stat.OQ, -50)));
        s.Experiments.Add(Exp("", (Stat.CD, 100)));
        var issues = Check(Doc(s));
        Assert.True(Has(issues, "mod.weight", Severity.Error));
        Assert.True(Has(issues, "exp.name", Severity.Error));
        Assert.DoesNotContain(issues, i => i.Code == "mod.sum" && i.Message.Contains("neg"));   // one problem, reported once
    }

    [Fact]
    public void An_experimental_can_raise_several_modifiers_each_with_its_own_percentages()
    {
        var s = Sch("a", "weaponsmith");
        s.Experiments.Add(ExpOf("Charges",
            Mod("Charges", (Stat.OQ, 67), (Stat.UT, 33)),
            Mod("Effective Range", (Stat.CD, 33), (Stat.OQ, 67))));
        s.Experiments.Add(Exp("Ease of Use", (Stat.OQ, 50), (Stat.SR, 50)));
        Assert.DoesNotContain(Check(Doc(s)), i => i.Severity == Severity.Error);

        s.Experiments[0].Modifiers[1].Weights[Stat.CD] = 32;   // only the second modifier is now wrong
        var sum = Assert.Single(Check(Doc(s)), i => i.Code == "mod.sum");
        Assert.Contains("Effective Range", sum.Message);
        Assert.Contains("Charges", sum.Message);
        Assert.Contains("99", sum.Message);
    }

    [Fact]
    public void Experimentals_need_modifiers_and_modifiers_need_unique_names()
    {
        var s = Sch("a", "weaponsmith");
        s.Experiments.Add(ExpOf("Nothing"));
        s.Experiments.Add(ExpOf("Charges", Mod("Charges", (Stat.OQ, 100)), Mod("charges", (Stat.UT, 100)), Mod("", (Stat.CD, 100))));
        var issues = Check(Doc(s));
        Assert.True(Has(issues, "exp.modifiers", Severity.Error));
        Assert.True(Has(issues, "mod.duplicate", Severity.Warning));
        Assert.True(Has(issues, "mod.name", Severity.Error));
    }

    [Fact]
    public void A_version_1_file_is_upgraded_to_one_modifier_per_experiment_and_saved_as_version_2()
    {
        var store = Store();
        SeedReferenceFiles(store);
        File.WriteAllText(store.SchematicsPath, """
            { "schemaVersion": 1, "schematics": [ { "id": "a", "name": "A", "professions": [ "weaponsmith" ],
              "experiments": [ { "name": "Charges", "weights": { "OQ": 67, "UT": 33 } },
                               { "name": "Ease of Use", "weights": { "SR": 100 } } ] } ] }
            """);

        var load = store.LoadSchematics();
        Assert.Null(load.Error);
        var exps = load.Value.Schematics.Single().Experiments;
        Assert.Equal(2, exps.Count);
        Assert.All(exps, e => Assert.Null(e.LegacyWeights));
        var charges = Assert.Single(exps[0].Modifiers);
        Assert.Equal("Charges", charges.Name);
        Assert.Equal(67, charges.Weights[Stat.OQ]);
        Assert.Equal(33, charges.Weights[Stat.UT]);
        Assert.Equal("Ease of Use", Assert.Single(exps[1].Modifiers).Name);

        Assert.True(store.SaveSchematics(load.Value).Saved);
        var text = File.ReadAllText(store.SchematicsPath);
        Assert.Contains($"\"schemaVersion\": {SchematicsDocument.CurrentSchemaVersion}", text);
        Assert.Contains("\"modifiers\"", text);
        var again = store.LoadSchematics().Value.Schematics.Single().Experiments;
        Assert.Equal(67, again[0].Modifiers.Single().Weights[Stat.OQ]);
    }

    [Fact]
    public void Old_percentages_next_to_modifiers_are_kept_and_reported_not_dropped()
    {
        var store = Store();
        SeedReferenceFiles(store);
        File.WriteAllText(store.SchematicsPath, """
            { "schemaVersion": 2, "schematics": [ { "id": "a", "name": "A", "professions": [ "weaponsmith" ],
              "experiments": [ { "name": "Charges", "weights": { "OQ": 100 },
                                 "modifiers": [ { "name": "Charges", "weights": { "UT": 100 } } ] } ] } ] }
            """);
        var doc = store.LoadSchematics().Value;
        var e = doc.Schematics.Single().Experiments.Single();
        Assert.NotNull(e.LegacyWeights);
        Assert.Single(e.Modifiers);
        var save = store.SaveSchematics(doc);
        Assert.False(save.Saved);
        Assert.Contains(save.Issues, i => i.Code == "exp.legacy");
    }

    // ------------------------------------------------------------ skills

    /// <summary>Weaponsmith and Armorsmith, each with two skills.</summary>
    static ProfessionsDocument SkilledProfessions()
    {
        var d = new ProfessionsDocument();
        foreach (var (id, name) in new[] { ("weaponsmith", "Weaponsmith"), ("armorsmith", "Armorsmith") })
        {
            var p = new Profession { Id = id, Name = name };
            p.Skills.Add(new ProfessionSkill { Id = id + "_novice", Name = "Novice " + name });
            p.Skills.Add(new ProfessionSkill { Id = id + "_master", Name = "Master " + name });
            d.Professions.Add(p);
        }
        return d;
    }

    static List<ValidationIssue> CheckSkills(Schematic s) =>
        SchematicValidator.Validate(Doc(s), new ProfessionCatalog(SkilledProfessions()), new ComponentClassesDocument(), new ResourceTree(Tree()));

    [Fact]
    public void A_schematic_without_a_skill_is_only_a_warning_for_now()
    {
        var issues = CheckSkills(Sch("a", "weaponsmith"));
        Assert.True(Has(issues, "skill.none", Severity.Warning));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Skills_from_ticked_professions_are_fine_including_several_professions()
    {
        var s = Sch("a", "weaponsmith", "armorsmith");
        s.Skills.AddRange(new[] { "weaponsmith_novice", "armorsmith_master" });
        Assert.DoesNotContain(CheckSkills(s), i => i.Code.StartsWith("skill."));
    }

    [Fact]
    public void A_skill_must_exist_and_its_profession_must_be_ticked()
    {
        var s = Sch("a", "weaponsmith");
        s.Skills.AddRange(new[] { "weaponsmith_novice", "armorsmith_novice", "chef_novice", "" });
        var issues = CheckSkills(s);
        var wrongProf = Assert.Single(issues, i => i.Code == "skill.profession");
        Assert.Equal(Severity.Error, wrongProf.Severity);
        Assert.Contains("Armorsmith", wrongProf.Message);
        Assert.True(Has(issues, "skill.unknown", Severity.Error));
        Assert.True(Has(issues, "skill.empty", Severity.Error));
    }

    [Fact]
    public void A_ticked_profession_without_one_of_its_skills_is_a_warning()
    {
        var s = Sch("a", "weaponsmith", "armorsmith");
        s.Skills.Add("weaponsmith_novice");               // nothing from Armorsmith
        var issues = CheckSkills(s);
        var w = Assert.Single(issues, i => i.Code == "prof.noskill");
        Assert.Equal(Severity.Warning, w.Severity);
        Assert.Contains("Armorsmith", w.Message);
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);

        s.Skills.Add("armorsmith_master");
        Assert.DoesNotContain(CheckSkills(s), i => i.Code == "prof.noskill");
    }

    [Fact]
    public void No_profession_skill_warning_while_the_schematic_has_no_skills_or_the_profession_has_none_defined()
    {
        // no skills at all: only skill.none
        var none = CheckSkills(Sch("a", "weaponsmith", "armorsmith"));
        Assert.True(Has(none, "skill.none", Severity.Warning));
        Assert.DoesNotContain(none, i => i.Code == "prof.noskill");

        // a profession with no skills defined (like Jedi Initiate today) cannot have one chosen
        var profs = SkilledProfessions();
        profs.Professions.Add(new Profession { Id = "jedi", Name = "Jedi" });
        var s = Sch("b", "weaponsmith", "jedi");
        s.Skills.Add("weaponsmith_novice");
        var issues = SchematicValidator.Validate(Doc(s), new ProfessionCatalog(profs), new ComponentClassesDocument(), new ResourceTree(Tree()));
        Assert.DoesNotContain(issues, i => i.Code == "prof.noskill");
    }

    [Fact]
    public void A_skill_listed_twice_is_a_warning()
    {
        var s = Sch("a", "weaponsmith");
        s.Skills.AddRange(new[] { "weaponsmith_novice", "weaponsmith_novice" });
        Assert.True(Has(CheckSkills(s), "skill.duplicate", Severity.Warning));
    }

    [Fact]
    public void Skills_are_saved_and_loaded_in_order()
    {
        var store = Store();
        SeedReferenceFiles(store);
        JsonFile.WriteAtomic(store.ProfessionsPath, SkilledProfessions());
        var s = Sch("a", "weaponsmith", "armorsmith");
        s.Skills.AddRange(new[] { "weaponsmith_master", "armorsmith_novice" });
        var save = store.SaveSchematics(Doc(s));
        Assert.True(save.Saved, string.Join("; ", save.Issues.Select(i => i.Message)));
        Assert.DoesNotContain(save.Issues, i => i.Code.StartsWith("skill."));

        var back = store.LoadSchematics().Value.Schematics.Single();
        Assert.Equal(new[] { "weaponsmith_master", "armorsmith_novice" }, back.Skills);
    }

    [Fact]
    public void A_version_2_file_without_skills_still_loads()
    {
        var store = Store();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.SchematicsPath, """
            { "schemaVersion": 2, "schematics": [ { "id": "a", "name": "A", "professions": [ "weaponsmith" ] } ] }
            """);
        var load = store.LoadSchematics();
        Assert.Null(load.Error);
        Assert.Empty(load.Value.Schematics.Single().Skills);
    }

    // ------------------------------------------------------------ tabs

    static TabsDocument Tabs(params (string Id, string Name)[] items)
    {
        var d = new TabsDocument();
        foreach (var (id, name) in items) d.Tabs.Add(new CraftTab { Id = id, Name = name });
        return d;
    }

    [Fact]
    public void The_tab_list_follows_the_same_rules_as_categories()
    {
        Assert.Empty(TabValidator.Validate(Tabs(("weapons", "Weapons"), ("armor", "Armor"))));
        Assert.True(Has(TabValidator.Validate(Tabs(("a", "Weapons"), ("a", "Armor"))), "id.duplicate", Severity.Error));
        Assert.True(Has(TabValidator.Validate(Tabs(("a", "Weapons"), ("b", "weapons"))), "name.duplicate", Severity.Error));
        Assert.True(Has(TabValidator.Validate(Tabs(("", "Weapons"))), "id.empty", Severity.Error));
        Assert.True(Has(TabValidator.Validate(Tabs()), "list.empty", Severity.Warning));
    }

    [Fact]
    public void A_schematic_tab_is_optional_but_must_be_in_the_list()
    {
        var tabs = new TabCatalog(Tabs(("weapons", "Weapons")));
        List<ValidationIssue> Run(string? tab)
        {
            var s = Sch("a", "weaponsmith");
            s.Tab = tab;
            return SchematicValidator.Validate(Doc(s), new ProfessionCatalog(Professions("weaponsmith")),
                                               new ComponentClassesDocument(), new ResourceTree(Tree()), null, tabs);
        }
        Assert.DoesNotContain(Run("weapons"), i => i.Code == "tab.unknown");
        Assert.DoesNotContain(Run(null), i => i.Code == "tab.unknown");
        Assert.True(Has(Run("food"), "tab.unknown", Severity.Error));
    }

    [Fact]
    public void Tabs_and_a_schematic_tab_are_saved_and_loaded()
    {
        var store = Store();
        SeedReferenceFiles(store);
        Assert.True(store.SaveTabs(Tabs(("weapons", "Weapons"), ("armor", "Armor"))).Saved);
        Assert.Equal(new[] { "weapons", "armor" }, store.LoadTabs().Value.Tabs.Select(t => t.Id).ToArray());

        var s = Sch("a", "weaponsmith");
        s.Tab = "armor";
        Assert.True(store.SaveSchematics(Doc(s)).Saved);
        Assert.Equal("armor", store.LoadSchematics().Value.Schematics.Single().Tab);

        s.Tab = "nope";   // checked against tabs.json on disk
        var refused = store.SaveSchematics(Doc(s));
        Assert.False(refused.Saved);
        Assert.Contains(refused.Issues, i => i.Code == "tab.unknown");
    }

    // ------------------------------------------------------------ ignore experimentation

    [Fact]
    public void The_schematic_flag_overrides_the_build_setting_to_ignore_experimentation()
    {
        var s = Sch("a", "artisan");
        s.Experiments.Add(Exp("Experimental Quality", (Stat.OQ, 100)));

        Assert.True(s.ExperimentationApplies(buildIgnoresExperimentation: false));
        Assert.False(s.ExperimentationApplies(buildIgnoresExperimentation: true));

        s.IgnoreExperimentation = true;   // the schematic wins: ignored either way
        Assert.False(s.ExperimentationApplies(buildIgnoresExperimentation: false));
        Assert.False(s.ExperimentationApplies(buildIgnoresExperimentation: true));

        Assert.False(Sch("b", "artisan").ExperimentationApplies(false));   // nothing to apply
    }

    [Fact]
    public void Ignore_experimentation_is_saved_only_when_set_and_keeps_the_experimentals()
    {
        var store = Store();
        SeedReferenceFiles(store);
        var plain = Sch("plain", "weaponsmith");
        var chair = Sch("chair", "weaponsmith");
        chair.IgnoreExperimentation = true;
        chair.Experiments.Add(Exp("Experimental Quality", (Stat.OQ, 100)));
        Assert.True(store.SaveSchematics(Doc(plain, chair)).Saved);

        var text = File.ReadAllText(store.SchematicsPath);
        Assert.Equal(1, text.Split("ignoreExperimentation").Length - 1);   // written for the chair only

        var back = store.LoadSchematics().Value.Schematics.ToDictionary(s => s.Id);
        Assert.True(back["chair"].IgnoreExperimentation);
        Assert.Single(back["chair"].Experiments);
        Assert.False(back["plain"].IgnoreExperimentation);
    }

    // ------------------------------------------------------------ looted items as components

    static List<ValidationIssue> CheckWithLoot(SchematicsDocument doc, LootItemsDocument? loot) =>
        SchematicValidator.Validate(doc, new ProfessionCatalog(Professions("weaponsmith")), new ComponentClassesDocument(),
                                    new ResourceTree(Tree()), null, null, loot);

    [Fact]
    public void A_looted_item_can_be_a_component_but_must_exist()
    {
        var s = Sch("a", "weaponsmith");
        s.Components.Add(Slot(SlotKind.LootItem, "precision_scope", 2, identical: true));
        var loot = Loot(("precision_scope", "Precision Tuned Weapon Scope"));
        Assert.DoesNotContain(CheckWithLoot(Doc(s), loot), i => i.Code.StartsWith("slot."));

        s.Components.Add(Slot(SlotKind.LootItem, "nope"));
        var unknown = Assert.Single(CheckWithLoot(Doc(s), loot), i => i.Code == "slot.loot.unknown");
        Assert.Equal(Severity.Error, unknown.Severity);
        Assert.DoesNotContain(CheckWithLoot(Doc(s), null), i => i.Code == "slot.loot.unknown");   // no list given: not checked
    }

    [Fact]
    public void A_looted_item_never_creates_a_loop_even_if_a_class_has_the_same_id()
    {
        var a = Sch("a", "weaponsmith");
        a.Components.Add(Slot(SlotKind.LootItem, "same"));
        var classes = new ComponentClassesDocument();
        var cls = new ComponentClass { Id = "same", Name = "Same" };
        cls.Members.Add(new ClassMember { Kind = MemberKind.Schematic, Id = "a" });
        classes.Classes.Add(cls);
        var issues = SchematicValidator.Validate(Doc(a), new ProfessionCatalog(Professions("weaponsmith")), classes,
                                                 new ResourceTree(Tree()), null, null, Loot(("same", "Same")));
        Assert.DoesNotContain(issues, i => i.Code.StartsWith("cycle"));
    }

    [Fact]
    public void Looted_item_components_are_saved_and_checked_against_lootitems_json()
    {
        var store = Store();
        SeedReferenceFiles(store);
        Assert.True(store.SaveLootItems(Loot(("precision_scope", "Precision Tuned Weapon Scope"))).Saved);

        var s = Sch("a", "weaponsmith");
        s.Components.Add(Slot(SlotKind.LootItem, "precision_scope"));
        Assert.True(store.SaveSchematics(Doc(s)).Saved);
        Assert.Contains("\"LootItem\"", File.ReadAllText(store.SchematicsPath));
        var back = store.LoadSchematics().Value.Schematics.Single().Components.Single();
        Assert.Equal(SlotKind.LootItem, back.Kind);
        Assert.Equal("precision_scope", back.Target);

        s.Components[0].Target = "nope";
        var refused = store.SaveSchematics(Doc(s));
        Assert.False(refused.Saved);
        Assert.Contains(refused.Issues, i => i.Code == "slot.loot.unknown");
    }

    [Fact]
    public void A_newer_schematics_file_is_refused()
    {
        var store = Store();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.SchematicsPath, "{ \"schemaVersion\": 99, \"schematics\": [] }");
        Assert.NotNull(store.LoadSchematics().Error);
    }

    [Fact]
    public void There_is_no_limit_of_four_experiments()
    {
        var s = Sch("a", "weaponsmith");
        for (int i = 1; i <= 7; i++) s.Experiments.Add(Exp($"line {i}", (Stat.OQ, 100)));
        Assert.DoesNotContain(Check(Doc(s)), i => i.Severity == Severity.Error);
    }

    [Fact]
    public void A_schematic_that_needs_itself_directly_is_an_error()
    {
        var a = Sch("a", "weaponsmith");
        var b = Sch("b", "weaponsmith");
        a.Components.Add(Slot(SlotKind.Schematic, "b"));
        b.Components.Add(Slot(SlotKind.Schematic, "a"));
        var issues = Check(Doc(a, b));
        Assert.Equal(2, issues.Count(i => i.Code == "cycle" && i.Severity == Severity.Error));

        var self = Sch("self", "weaponsmith");
        self.Components.Add(Slot(SlotKind.Schematic, "self"));
        Assert.True(Has(Check(Doc(self)), "cycle", Severity.Error));
    }

    [Fact]
    public void A_loop_only_through_a_class_is_a_warning_not_an_error()
    {
        // "Advanced Vibro" needs a Vibro-class component, and the class lists Advanced Vibro itself.
        var basic = Sch("vibro", "weaponsmith");
        var advanced = Sch("advanced_vibro", "weaponsmith");
        advanced.Components.Add(Slot(SlotKind.Class, "vibro_class"));
        var classes = Classes(Class("vibro_class", (MemberKind.Schematic, "vibro"), (MemberKind.Schematic, "advanced_vibro")));

        var issues = Check(Doc(basic, advanced), classes);
        Assert.True(Has(issues, "cycle.class", Severity.Warning));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
        Assert.DoesNotContain(issues, i => i.Code == "cycle");
    }

    [Fact]
    public void Chains_without_loops_are_clean()
    {
        var a = Sch("a", "weaponsmith");
        var b = Sch("b", "weaponsmith");
        var c = Sch("c", "weaponsmith");
        a.Components.Add(Slot(SlotKind.Schematic, "b"));
        a.Components.Add(Slot(SlotKind.Schematic, "c"));
        b.Components.Add(Slot(SlotKind.Schematic, "c"));   // c reached twice (diamond), still no loop
        var issues = Check(Doc(a, b, c));
        Assert.DoesNotContain(issues, i => i.Code.StartsWith("cycle"));
    }

    [Fact]
    public void Duplicate_ids_are_errors_and_duplicate_names_only_warnings()
    {
        var x = Sch("x", "weaponsmith");
        var y = Sch("x", "weaponsmith");
        Assert.True(Has(Check(Doc(x, y)), "id.duplicate", Severity.Error));

        var p = Sch("p", "weaponsmith"); p.Name = "Same";
        var q = Sch("q", "weaponsmith"); q.Name = "same";
        var issues = Check(Doc(p, q));
        Assert.True(Has(issues, "name.duplicate", Severity.Warning));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }


    // ------------------------------------------------------------ categories

    [Fact]
    public void Category_list_rules()
    {
        Assert.Empty(CategoryValidator.Validate(Categories(("rifles", "Rifles"), ("armor", "Armor"))));
        Assert.True(Has(CategoryValidator.Validate(Categories(("a", "X"), ("a", "Y"))), "id.duplicate", Severity.Error));
        Assert.True(Has(CategoryValidator.Validate(Categories(("a", "X"), ("b", "x"))), "name.duplicate", Severity.Error));
        Assert.True(Has(CategoryValidator.Validate(Categories(("", "X"))), "id.empty", Severity.Error));
        Assert.True(Has(CategoryValidator.Validate(Categories(("a", " "))), "name.empty", Severity.Error));
        Assert.True(Has(CategoryValidator.Validate(Categories(("Heavy Armor", "Heavy Armor"))), "id.format", Severity.Warning));
        Assert.True(Has(CategoryValidator.Validate(Categories()), "list.empty", Severity.Warning));
    }

    [Fact]
    public void A_schematic_category_must_be_in_the_list_when_one_is_supplied()
    {
        var cats = new CategoryCatalog(Categories(("rifles", "Rifles")));
        List<ValidationIssue> Run(string? category)
        {
            var s = Sch("a", "weaponsmith");
            s.Category = category;
            return SchematicValidator.Validate(Doc(s), new ProfessionCatalog(Professions("weaponsmith")),
                                               new ComponentClassesDocument(), new ResourceTree(Tree()), cats);
        }

        Assert.DoesNotContain(Run("rifles"), i => i.Severity == Severity.Error);
        Assert.DoesNotContain(Run(null), i => i.Severity == Severity.Error);       // a category is optional
        Assert.DoesNotContain(Run("  "), i => i.Severity == Severity.Error);
        Assert.True(Has(Run("Rifles"), "category.unknown", Severity.Error));      // the ID is lowercase; the editor converts names to IDs
        Assert.True(Has(Run("pistols"), "category.unknown", Severity.Error));
    }

    [Fact]
    public void The_category_check_is_skipped_when_no_list_is_supplied()
    {
        var s = Sch("a", "weaponsmith");
        s.Category = "anything";
        Assert.DoesNotContain(Check(Doc(s)), i => i.Code == "category.unknown");
    }

    [Fact]
    public void Categories_round_trip_and_gate_schematic_saving()
    {
        var store = Store();
        SeedReferenceFiles(store);   // writes categories.json with rifles + armor

        var good = Sch("a", "weaponsmith");
        good.Category = "armor";
        Assert.True(store.SaveSchematics(Doc(good)).Saved);

        var bad = Sch("b", "weaponsmith");
        bad.Category = "Pistols";
        var refused = store.SaveSchematics(Doc(good, bad));
        Assert.False(refused.Saved);
        Assert.Contains(refused.Issues, i => i.Code == "category.unknown");

        Assert.True(store.SaveCategories(Categories(("rifles", "Rifles"), ("armor", "Armor"), ("pistols", "Pistols"))).Saved);
        bad.Category = "pistols";
        Assert.True(store.SaveSchematics(Doc(good, bad)).Saved);
        Assert.Equal(new[] { "rifles", "armor", "pistols" }, store.LoadCategories().Value.Categories.Select(c => c.Id));
    }

    [Fact]
    public void Categories_refuse_to_save_with_errors()
    {
        var store = Store();
        Assert.False(store.SaveCategories(Categories(("a", "X"), ("a", "Y"))).Saved);
        Assert.False(File.Exists(store.CategoriesPath));
    }

    // ------------------------------------------------------------ store

    StaticDataStore Store() => new(_folder);

    void SeedReferenceFiles(StaticDataStore store)
    {
        JsonFile.WriteAtomic(store.ProfessionsPath, Professions("weaponsmith", "armorsmith"));
        JsonFile.WriteAtomic(store.ResourcesPath, Tree());
        JsonFile.WriteAtomic(store.CategoriesPath, Categories(("rifles", "Rifles"), ("armor", "Armor")));
    }

    [Fact]
    public void Missing_files_load_as_empty_documents()
    {
        var store = Store();
        Assert.False(store.LoadLootItems().FileExisted);
        Assert.Empty(store.LoadLootItems().Value.Items);
        Assert.Empty(store.LoadClasses().Value.Classes);
        Assert.Empty(store.LoadSchematics().Value.Schematics);
        Assert.Empty(store.LoadCategories().Value.Categories);
    }

    [Fact]
    public void Loot_and_classes_round_trip()
    {
        var store = Store();
        Assert.True(store.SaveLootItems(Loot(("light_blade", "Light Weight Blade Vibro Unit"))).Saved);
        Assert.False(store.SaveSchematics(Doc(Sch("blade_vibro", "weaponsmith"))).Saved);   // professions.json not there yet: refused
        SeedReferenceFiles(store);
        Assert.True(store.SaveSchematics(Doc(Sch("blade_vibro", "weaponsmith"))).Saved);

        var classes = Classes(Class("vibro", (MemberKind.Schematic, "blade_vibro"), (MemberKind.LootItem, "light_blade")));
        Assert.True(store.SaveClasses(classes).Saved);

        var back = store.LoadClasses().Value.Classes.Single();
        Assert.Equal("vibro", back.Id);
        Assert.Equal(new[] { MemberKind.Schematic, MemberKind.LootItem }, back.Members.Select(m => m.Kind));
        Assert.Equal(new[] { "blade_vibro", "light_blade" }, back.Members.Select(m => m.Id));
    }

    [Fact]
    public void Classes_refuse_to_save_with_a_member_that_does_not_exist_on_disk()
    {
        var store = Store();
        var result = store.SaveClasses(Classes(Class("c", (MemberKind.LootItem, "ghost"))));
        Assert.False(result.Saved);
        Assert.False(File.Exists(store.ClassesPath));
    }

    [Fact]
    public void The_beam_rifle_round_trips_through_json()
    {
        var store = Store();
        SeedReferenceFiles(store);
        var (doc, classes) = BeamRifleWorld();

        Assert.True(store.SaveSchematics(Doc(doc.Schematics.Skip(1).ToArray())).Saved);   // dependencies first
        Assert.True(store.SaveClasses(classes).Saved);
        var save = store.SaveSchematics(doc);
        Assert.True(save.Saved, string.Join("; ", save.Issues.Select(i => i.Message)));

        var back = store.LoadSchematics().Value.Schematics.Single(s => s.Id == "beam_rifle");
        Assert.Equal(new[] { "weaponsmith" }, back.Professions);
        Assert.Equal("rifles", back.Category);
        Assert.False(back.ShowOverride!.Value);
        Assert.Null(back.Complexity);
        Assert.Equal(4, back.Components.Count);
        Assert.Equal(SlotKind.Class, back.Components[2].Kind);
        Assert.True(back.Components[2].Optional);
        Assert.Equal(4, back.Components[0].Quantity);
        Assert.Equal(3, back.Resources.Count);
        var exp = Assert.Single(back.Experiments);
        var mod = Assert.Single(exp.Modifiers);
        Assert.Equal(exp.Name, mod.Name);
        Assert.Equal(50, mod.Weights[Stat.CD]);
        Assert.Equal(50, mod.Weights[Stat.OQ]);
        Assert.Equal(2, mod.Weights.Count);
        Assert.Null(exp.LegacyWeights);
    }

    [Fact]
    public void Default_flags_are_not_written_and_stats_use_their_codes()
    {
        var store = Store();
        SeedReferenceFiles(store);
        var s = Sch("a", "weaponsmith");
        s.Components.Add(Slot(SlotKind.Schematic, "a2"));          // identical and optional are both false here
        s.Experiments.Add(Exp("x", (Stat.MB, 100)));
        Assert.True(store.SaveSchematics(Doc(s, Sch("a2", "weaponsmith"))).Saved);

        var text = File.ReadAllText(store.SchematicsPath);
        Assert.DoesNotContain("identical", text);
        Assert.DoesNotContain("optional", text);
        Assert.Contains("\"MB\": 100", text);
    }

    [Fact]
    public void Schematics_refuse_to_save_with_errors_and_keep_the_old_file()
    {
        var store = Store();
        SeedReferenceFiles(store);
        Assert.True(store.SaveSchematics(Doc(Sch("good", "weaponsmith"))).Saved);
        var before = File.ReadAllText(store.SchematicsPath);

        var bad = Sch("bad");   // no profession
        Assert.False(store.SaveSchematics(Doc(Sch("good", "weaponsmith"), bad)).Saved);
        Assert.Equal(before, File.ReadAllText(store.SchematicsPath));
    }

    [Fact]
    public void A_misspelled_property_in_a_hand_edited_file_is_reported_not_ignored()
    {
        var store = Store();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.SchematicsPath, "{ \"schemaVersion\": 1, \"schematics\": [ { \"id\": \"a\", \"nmae\": \"A\" } ] }");
        var load = store.LoadSchematics();
        Assert.NotNull(load.Error);
        Assert.Empty(load.Value.Schematics);
    }

    [Fact]
    public void An_unreadable_dependency_blocks_saving_with_a_clear_reason()
    {
        var store = Store();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.ClassesPath, "not json");
        JsonFile.WriteAtomic(store.ProfessionsPath, Professions("weaponsmith"));
        var result = store.SaveSchematics(Doc(Sch("a", "weaponsmith")));
        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Code == "dep.load");
    }
}

public sealed class ResourceLabelTests
{
    static ResourceNode N(string id, string name, params ResourceNode[] kids) =>
        new() { Id = id, Name = name, Children = kids.Length == 0 ? null : kids.ToList() };

    [Fact]
    public void Unique_names_are_shown_without_their_path()
    {
        var doc = new ResourceTreeDocument();
        doc.Nodes.Add(N("inorganic", "Inorganic", N("mineral", "Mineral", N("metal", "Metal", N("ferrous", "Ferrous Metal", N("iron", "Iron"))))));
        var labels = ResourceLabels.Build(doc);
        Assert.Equal("Ferrous Metal", labels["ferrous"]);
        Assert.Equal("Iron", labels["iron"]);
        Assert.Equal("Inorganic", labels["inorganic"]);
    }

    [Fact]
    public void A_shared_name_gets_just_enough_parents_to_tell_it_apart()
    {
        var doc = new ResourceTreeDocument();
        doc.Nodes.Add(N("ferrous", "Ferrous Metal", N("steel_f", "Steel")));
        doc.Nodes.Add(N("alloy", "Alloy", N("steel_a", "Steel")));
        var labels = ResourceLabels.Build(doc);
        Assert.Equal("Steel [Ferrous Metal]", labels["steel_f"]);
        Assert.Equal("Steel [Alloy]", labels["steel_a"]);
    }

    [Fact]
    public void Deeper_ancestors_are_added_only_when_the_parent_is_not_enough()
    {
        var doc = new ResourceTreeDocument();
        doc.Nodes.Add(N("a", "Metal", N("a1", "Group", N("a2", "Steel"))));
        doc.Nodes.Add(N("b", "Gem", N("b1", "Group", N("b2", "Steel"))));
        var labels = ResourceLabels.Build(doc);
        Assert.Equal("Steel [Metal > Group]", labels["a2"]);
        Assert.Equal("Steel [Gem > Group]", labels["b2"]);
        Assert.Equal("Group [Metal]", labels["a1"]);
    }
}
