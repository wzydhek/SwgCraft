using SwgCraft.Core;

namespace SwgCraft.Tests;

public sealed class ProfessionTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static ProfessionsDocument Doc(params (string Id, string Name)[] items)
    {
        var d = new ProfessionsDocument();
        foreach (var (id, name) in items) d.Professions.Add(new Profession { Id = id, Name = name });
        return d;
    }

    static bool Has(IEnumerable<ValidationIssue> issues, string code, Severity severity) =>
        issues.Any(i => i.Code == code && i.Severity == severity);

    // ------------------------------------------------------------ validator

    [Fact]
    public void A_normal_list_has_no_issues() =>
        Assert.Empty(ProfessionValidator.Validate(Doc(("armorsmith", "Armorsmith"), ("droid_engineer", "Droid Engineer"))));

    [Fact]
    public void Duplicate_ids_are_errors() =>
        Assert.True(Has(ProfessionValidator.Validate(Doc(("chef", "Chef"), ("chef", "Cook"))), "id.duplicate", Severity.Error));

    [Fact]
    public void Duplicate_names_are_errors_ignoring_case() =>
        Assert.True(Has(ProfessionValidator.Validate(Doc(("a", "Chef"), ("b", "chef"))), "name.duplicate", Severity.Error));

    [Fact]
    public void Empty_id_or_name_is_an_error()
    {
        Assert.True(Has(ProfessionValidator.Validate(Doc(("", "Chef"))), "id.empty", Severity.Error));
        Assert.True(Has(ProfessionValidator.Validate(Doc(("chef", "  "))), "name.empty", Severity.Error));
    }

    [Fact]
    public void Odd_ids_are_only_a_warning()
    {
        var issues = ProfessionValidator.Validate(Doc(("Droid Engineer", "Droid Engineer")));
        Assert.True(Has(issues, "id.format", Severity.Warning));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void An_empty_list_is_only_a_warning()
    {
        var issues = ProfessionValidator.Validate(Doc());
        Assert.True(Has(issues, "list.empty", Severity.Warning));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Issues_point_at_the_profession_they_belong_to()
    {
        var doc = Doc(("a", "Chef"), ("b", "chef"));
        var issues = ProfessionValidator.Validate(doc);
        var subjects = issues.Where(i => i.Code == "name.duplicate").Select(i => i.Subject).ToList();
        Assert.Contains(doc.Professions[0], subjects);
        Assert.Contains(doc.Professions[1], subjects);
    }

    // ------------------------------------------------------------ catalog

    [Fact]
    public void Catalog_keeps_order_and_finds_by_id()
    {
        var catalog = new ProfessionCatalog(Doc(("tailor", "Tailor"), ("chef", "Chef"), ("architect", "Architect")));
        Assert.Equal(new[] { "tailor", "chef", "architect" }, catalog.All.Select(p => p.Id).ToArray());
        Assert.Equal("Chef", catalog.Find("chef")!.Name);
        Assert.Null(catalog.Find("nobody"));
        Assert.Empty(ProfessionCatalog.Empty.All);
    }

    // ------------------------------------------------------------ files

    [Fact]
    public void Save_and_load_round_trip_in_order()
    {
        var store = new StaticDataStore(_folder);
        var saved = store.SaveProfessions(Doc(("droid_engineer", "Droid Engineer"), ("chef", "Chef")));
        Assert.True(saved.Saved);
        Assert.True(File.Exists(store.ProfessionsPath));

        var text = File.ReadAllText(store.ProfessionsPath);
        Assert.Contains("\"professions\"", text);
        Assert.Contains("\"droid_engineer\"", text);

        var load = store.LoadProfessions();
        Assert.Null(load.Error);
        Assert.True(load.FileExisted);
        Assert.Equal(new[] { "droid_engineer", "chef" }, load.Value.Professions.Select(p => p.Id).ToArray());
        Assert.Equal("Droid Engineer", load.Value.Professions[0].Name);
    }

    [Fact]
    public void Saving_with_errors_writes_nothing()
    {
        var store = new StaticDataStore(_folder);
        var result = store.SaveProfessions(Doc(("a", "Chef"), ("a", "Tailor")));
        Assert.False(result.Saved);
        Assert.False(File.Exists(store.ProfessionsPath));
    }

    [Fact]
    public void A_missing_file_loads_as_an_empty_list_without_error()
    {
        var load = new StaticDataStore(_folder).LoadProfessions();
        Assert.False(load.FileExisted);
        Assert.Null(load.Error);
        Assert.Empty(load.Value.Professions);
    }

    [Fact]
    public void A_misspelled_property_is_an_error_instead_of_being_ignored()
    {
        Directory.CreateDirectory(_folder);
        var store = new StaticDataStore(_folder);
        File.WriteAllText(store.ProfessionsPath, """{ "schemaVersion": 1, "profession": [ { "id": "chef", "name": "Chef" } ] }""");
        Assert.NotNull(store.LoadProfessions().Error);
    }

    [Fact]
    public void A_newer_schema_version_is_refused()
    {
        Directory.CreateDirectory(_folder);
        var store = new StaticDataStore(_folder);
        File.WriteAllText(store.ProfessionsPath, """{ "schemaVersion": 99, "professions": [] }""");
        Assert.NotNull(store.LoadProfessions().Error);
    }

    [Fact]
    public void Resources_still_load_and_save_after_the_store_refactor()
    {
        var store = new StaticDataStore(_folder);
        var leaf = new ResourceNode
        {
            Id = "steel", Name = "Steel",
            ExternalIds = new() { [ExternalSources.GalaxyHarvester] = "steel_gh" },
            Ranges = new() { [Stat.OQ] = new StatRange { Min = 1, Max = 1000 } },
        };
        Assert.True(store.SaveResources(new ResourceTreeDocument { Nodes = { leaf } }).Saved);
        var load = store.LoadResources();
        Assert.Null(load.Error);
        Assert.Equal("steel", Assert.Single(load.Value.Nodes).Id);
    }

    // ------------------------------------------------------------ settings and ids

    [Fact]
    public void Professions_are_enabled_unless_listed_as_disabled()
    {
        var s = new AppSettings();
        Assert.True(s.IsProfessionEnabled("chef"));
        s.DisabledProfessions.Add("chef");
        Assert.False(s.IsProfessionEnabled("chef"));
        Assert.True(s.IsProfessionEnabled("tailor"));
    }

    [Fact]
    public void Old_settings_files_without_the_property_still_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "schemaVersion": 1 }""");
            var s = JsonFile.Read<AppSettings>(path);
            Assert.Empty(s.DisabledProfessions);
            Assert.True(s.IsProfessionEnabled("chef"));

            s.DisabledProfessions.Add("chef");
            JsonFile.WriteAtomic(path, s);
            Assert.False(JsonFile.Read<AppSettings>(path).IsProfessionEnabled("chef"));
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    // ------------------------------------------------------------ skills

    static Profession WithSkills(string id, string name, params (string Id, string Name)[] skills)
    {
        var p = new Profession { Id = id, Name = name };
        foreach (var (sid, sname) in skills) p.Skills.Add(new ProfessionSkill { Id = sid, Name = sname });
        return p;
    }

    static ProfessionsDocument DocOf(params Profession[] professions)
    {
        var d = new ProfessionsDocument();
        d.Professions.AddRange(professions);
        return d;
    }

    [Fact]
    public void Skill_ids_are_built_from_the_profession_id_and_the_skill_name()
    {
        Assert.Equal("artisan_novice_artisan", ProfessionSkill.IdFor("artisan", "Novice Artisan"));
        Assert.Equal("bio_engineer_engineering_i", ProfessionSkill.IdFor("bio_engineer", "Engineering I"));
        Assert.Equal("artisan_", ProfessionSkill.IdFor("artisan", ""));
    }

    [Fact]
    public void Skills_that_follow_the_rules_have_no_issues() =>
        Assert.Empty(ProfessionValidator.Validate(DocOf(
            WithSkills("artisan", "Artisan", ("artisan_novice_artisan", "Novice Artisan"), ("artisan_master_artisan", "Master Artisan")),
            WithSkills("chef", "Chef", ("chef_novice_chef", "Novice Chef")))));

    [Fact]
    public void A_skill_id_must_start_with_its_profession_id()
    {
        var issues = ProfessionValidator.Validate(DocOf(WithSkills("artisan", "Artisan", ("novice_artisan", "Novice Artisan"))));
        var issue = Assert.Single(issues, i => i.Code == "skill.id.prefix");
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Contains("artisan_novice_artisan", issue.Message);   // suggests the right ID

        // "artisanx_..." is not "artisan_..."
        Assert.True(Has(ProfessionValidator.Validate(DocOf(WithSkills("artisan", "Artisan", ("artisanx_novice", "Novice")))), "skill.id.prefix", Severity.Error));
    }

    [Fact]
    public void Skills_need_a_name_an_id_and_something_after_the_prefix()
    {
        var issues = ProfessionValidator.Validate(DocOf(WithSkills("artisan", "Artisan", ("artisan_", ""), ("", "Novice Artisan"))));
        Assert.True(Has(issues, "skill.name.empty", Severity.Error));
        Assert.True(Has(issues, "skill.id.short", Severity.Error));
        Assert.True(Has(issues, "skill.id.empty", Severity.Error));
    }

    [Fact]
    public void Skill_names_are_unique_within_a_profession_and_ids_unique_overall()
    {
        var sameName = ProfessionValidator.Validate(DocOf(
            WithSkills("artisan", "Artisan", ("artisan_novice", "Novice"), ("artisan_novice_2", "novice"))));
        Assert.True(Has(sameName, "skill.name.duplicate", Severity.Error));

        var sameId = ProfessionValidator.Validate(DocOf(
            WithSkills("artisan", "Artisan", ("artisan_novice", "Novice"), ("artisan_novice", "Novice Two"))));
        Assert.True(Has(sameId, "skill.id.duplicate", Severity.Error));

        // the same skill name in two professions is fine
        Assert.Empty(ProfessionValidator.Validate(DocOf(
            WithSkills("artisan", "Artisan", ("artisan_novice", "Novice")),
            WithSkills("chef", "Chef", ("chef_novice", "Novice")))));
    }

    [Fact]
    public void Skill_issues_point_at_the_profession_they_belong_to()
    {
        var chef = WithSkills("chef", "Chef", ("novice_chef", "Novice Chef"));
        var issues = ProfessionValidator.Validate(DocOf(WithSkills("artisan", "Artisan"), chef));
        Assert.Same(chef, Assert.Single(issues, i => i.Code == "skill.id.prefix").Subject);
    }

    [Fact]
    public void Skills_round_trip_in_order_and_the_catalog_finds_them()
    {
        var store = new StaticDataStore(_folder);
        var doc = DocOf(WithSkills("artisan", "Artisan",
            ("artisan_novice_artisan", "Novice Artisan"), ("artisan_engineering_i", "Engineering I"), ("artisan_master_artisan", "Master Artisan")));
        Assert.True(store.SaveProfessions(doc).Saved);
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(store.ProfessionsPath));

        var back = store.LoadProfessions().Value;
        Assert.Equal(new[] { "artisan_novice_artisan", "artisan_engineering_i", "artisan_master_artisan" },
                     back.Professions.Single().Skills.Select(s => s.Id).ToArray());
        var hit = new ProfessionCatalog(back).FindSkill("artisan_engineering_i");
        Assert.NotNull(hit);
        Assert.Equal("artisan", hit.Value.Profession.Id);
        Assert.Equal("Engineering I", hit.Value.Skill.Name);
        Assert.Null(new ProfessionCatalog(back).FindSkill("nope"));
    }

    [Fact]
    public void A_version_1_file_without_skills_still_loads()
    {
        Directory.CreateDirectory(_folder);
        var store = new StaticDataStore(_folder);
        File.WriteAllText(store.ProfessionsPath, """{ "schemaVersion": 1, "professions": [ { "id": "chef", "name": "Chef" } ] }""");
        var load = store.LoadProfessions();
        Assert.Null(load.Error);
        Assert.Empty(load.Value.Professions.Single().Skills);
    }

    [Fact]
    public void Names_become_ids()
    {
        Assert.Equal("droid_engineer", Slug.From("Droid Engineer"));
        Assert.Equal("bio_engineer", Slug.From("Bio-Engineer"));
    }
}
