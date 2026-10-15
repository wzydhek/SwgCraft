using System.Text.Json;

namespace SwgCraft.Core;

public sealed record LoadResult<T>(T Value, bool FileExisted, string? Error);
public sealed record SaveResult(bool Saved, IReadOnlyList<ValidationIssue> Issues);

/// <summary>Reads and writes the static JSON files (resources, professions, categories, tabs, lootitems, componentclasses, schematics).</summary>
public sealed class StaticDataStore
{
    public string Folder { get; }
    public string ResourcesPath => Path.Combine(Folder, "resources.json");
    public string ProfessionsPath => Path.Combine(Folder, "professions.json");
    public string LootItemsPath => Path.Combine(Folder, "lootitems.json");
    public string ClassesPath => Path.Combine(Folder, "componentclasses.json");
    public string SchematicsPath => Path.Combine(Folder, "schematics.json");
    public string CategoriesPath => Path.Combine(Folder, "categories.json");
    public string TabsPath => Path.Combine(Folder, "tabs.json");
    public string StringsPath => Path.Combine(Folder, "strings.json");

    public StaticDataStore(string folder) => Folder = folder;

    // ------------------------------------------------------------ load

    public LoadResult<ResourceTreeDocument> LoadResources() =>
        Load<ResourceTreeDocument>(ResourcesPath, d => d.SchemaVersion, ResourceTreeDocument.CurrentSchemaVersion);

    public LoadResult<ProfessionsDocument> LoadProfessions() =>
        Load<ProfessionsDocument>(ProfessionsPath, d => d.SchemaVersion, ProfessionsDocument.CurrentSchemaVersion);

    public LoadResult<CategoriesDocument> LoadCategories() =>
        Load<CategoriesDocument>(CategoriesPath, d => d.SchemaVersion, CategoriesDocument.CurrentSchemaVersion);

    public LoadResult<StringsDocument> LoadStrings() =>
        Load<StringsDocument>(StringsPath, d => d.SchemaVersion, StringsDocument.CurrentSchemaVersion);

    public LoadResult<TabsDocument> LoadTabs() =>
        Load<TabsDocument>(TabsPath, d => d.SchemaVersion, TabsDocument.CurrentSchemaVersion);

    public LoadResult<LootItemsDocument> LoadLootItems() =>
        Load<LootItemsDocument>(LootItemsPath, d => d.SchemaVersion, LootItemsDocument.CurrentSchemaVersion);

    public LoadResult<ComponentClassesDocument> LoadClasses() =>
        Load<ComponentClassesDocument>(ClassesPath, d => d.SchemaVersion, ComponentClassesDocument.CurrentSchemaVersion);

    /// <summary>Older files are upgraded in memory (see <see cref="SchematicUpgrade"/>); the file itself changes on the next save.</summary>
    public LoadResult<SchematicsDocument> LoadSchematics()
    {
        var r = Load<SchematicsDocument>(SchematicsPath, d => d.SchemaVersion, SchematicsDocument.CurrentSchemaVersion);
        if (r.Error == null) SchematicUpgrade.Upgrade(r.Value);
        return r;
    }

    // ------------------------------------------------------------ validate (against what is saved on disk)

    public List<ValidationIssue> ValidateCategories(CategoriesDocument doc) => CategoryValidator.Validate(doc);

    public List<ValidationIssue> ValidateTabs(TabsDocument doc) => TabValidator.Validate(doc);

    public List<ValidationIssue> ValidateStrings(StringsDocument doc) => StringsValidator.Validate(doc);

    public List<ValidationIssue> ValidateLootItems(LootItemsDocument doc) => LootItemValidator.Validate(doc);

    /// <summary>Checks class members against the schematics and looted items saved on disk.</summary>
    public List<ValidationIssue> ValidateClasses(ComponentClassesDocument doc)
    {
        var schematics = LoadSchematics();
        var loot = LoadLootItems();
        var issues = new List<ValidationIssue>();
        AddLoadErrors(issues, schematics.Error, loot.Error);
        issues.AddRange(ClassValidator.Validate(doc, schematics.Error == null ? schematics.Value : null, loot.Error == null ? loot.Value : null));
        return issues;
    }

    /// <summary>Checks schematic references against the professions, classes, looted items, resource tree, categories and tabs saved on disk.</summary>
    public List<ValidationIssue> ValidateSchematics(SchematicsDocument doc)
    {
        var professions = LoadProfessions();
        var classes = LoadClasses();
        var resources = LoadResources();
        var categories = LoadCategories();
        var tabs = LoadTabs();
        var loot = LoadLootItems();
        var issues = new List<ValidationIssue>();
        AddLoadErrors(issues, professions.Error, classes.Error, resources.Error, categories.Error, tabs.Error, loot.Error);
        issues.AddRange(SchematicValidator.Validate(doc, new ProfessionCatalog(professions.Value), classes.Value,
                                                    new ResourceTree(resources.Value), new CategoryCatalog(categories.Value),
                                                    new TabCatalog(tabs.Value), loot.Error == null ? loot.Value : null));
        return issues;
    }

    static void AddLoadErrors(List<ValidationIssue> issues, params string?[] errors)
    {
        foreach (var e in errors)
            if (e != null) issues.Add(new ValidationIssue(Severity.Error, "dep.load", "A file this one depends on could not be read, so references cannot be checked: " + e));
    }

    // ------------------------------------------------------------ save (refuses on errors, so files on disk always load)

    public SaveResult SaveResources(ResourceTreeDocument doc)
    {
        var issues = ResourceTreeValidator.Validate(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = ResourceTreeDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(ResourcesPath, doc);
        return new SaveResult(true, issues);
    }

    public SaveResult SaveProfessions(ProfessionsDocument doc)
    {
        var issues = ProfessionValidator.Validate(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = ProfessionsDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(ProfessionsPath, doc);
        return new SaveResult(true, issues);
    }

    public SaveResult SaveCategories(CategoriesDocument doc)
    {
        var issues = CategoryValidator.Validate(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = CategoriesDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(CategoriesPath, doc);
        return new SaveResult(true, issues);
    }

    public SaveResult SaveStrings(StringsDocument doc)
    {
        var issues = StringsValidator.Validate(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = StringsDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(StringsPath, doc);
        return new SaveResult(true, issues);
    }

    public SaveResult SaveTabs(TabsDocument doc)
    {
        var issues = TabValidator.Validate(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = TabsDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(TabsPath, doc);
        return new SaveResult(true, issues);
    }

    public SaveResult SaveLootItems(LootItemsDocument doc)
    {
        var issues = ValidateLootItems(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = LootItemsDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(LootItemsPath, doc);
        return new SaveResult(true, issues);
    }

    public SaveResult SaveClasses(ComponentClassesDocument doc)
    {
        var issues = ValidateClasses(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = ComponentClassesDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(ClassesPath, doc);
        return new SaveResult(true, issues);
    }

    public SaveResult SaveSchematics(SchematicsDocument doc)
    {
        var issues = ValidateSchematics(doc);
        if (issues.Any(i => i.Severity == Severity.Error)) return new SaveResult(false, issues);
        doc.SchemaVersion = SchematicsDocument.CurrentSchemaVersion;
        JsonFile.WriteAtomic(SchematicsPath, doc);
        return new SaveResult(true, issues);
    }

    static LoadResult<T> Load<T>(string path, Func<T, int> versionOf, int currentVersion) where T : new()
    {
        if (!File.Exists(path)) return new(new T(), false, null);
        try
        {
            var doc = JsonFile.Read<T>(path);
            if (versionOf(doc) > currentVersion)
                return new(new T(), true, $"{path} has schemaVersion {versionOf(doc)}, newer than this app understands.");
            return new(doc, true, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(new T(), true, $"Could not read {path}:\n{ex.Message}");
        }
    }
}
