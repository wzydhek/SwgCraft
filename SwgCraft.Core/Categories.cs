using System.Text.RegularExpressions;

namespace SwgCraft.Core;

/// <summary>Persisted shape of categories.json: the list of categories a crafted item can belong to (Rifles, Armor, Food, ...).</summary>
public sealed class CategoriesDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CraftCategory> Categories { get; set; } = new();
}

public sealed class CraftCategory
{
    /// <summary>Stable key that schematics refer to (their "category" field). Don't rename it once schematics use it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Read-only lookup over a categories document.</summary>
public sealed class CategoryCatalog
{
    public static CategoryCatalog Empty { get; } = new(new CategoriesDocument());

    readonly Dictionary<string, CraftCategory> _byId = new(StringComparer.Ordinal);

    public CategoriesDocument Document { get; }
    public IReadOnlyList<CraftCategory> All => Document.Categories;

    public CategoryCatalog(CategoriesDocument document)
    {
        Document = document;
        foreach (var c in document.Categories)
            if (!string.IsNullOrEmpty(c.Id)) _byId.TryAdd(c.Id, c);
    }

    public CraftCategory? Find(string id) => _byId.GetValueOrDefault(id);
}

public static class CategoryValidator
{
    static readonly Regex IdPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    public static List<ValidationIssue> Validate(CategoriesDocument doc)
    {
        var issues = new List<ValidationIssue>();
        var ids = new Dictionary<string, List<CraftCategory>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<CraftCategory>>(StringComparer.OrdinalIgnoreCase);

        void Add(Severity s, string code, string msg, CraftCategory c) => issues.Add(new ValidationIssue(s, code, msg, c));

        if (doc.Categories.Count == 0)
            issues.Add(new ValidationIssue(Severity.Warning, "list.empty", "No categories are defined yet."));

        foreach (var c in doc.Categories)
        {
            var id = c.Id?.Trim() ?? "";
            var name = c.Name?.Trim() ?? "";

            if (id.Length == 0) Add(Severity.Error, "id.empty", $"'{(name.Length == 0 ? "(unnamed)" : name)}' has no ID.", c);
            else
            {
                if (!ids.TryGetValue(id, out var l)) ids[id] = l = new List<CraftCategory>();
                l.Add(c);
                if (!IdPattern.IsMatch(id))
                    Add(Severity.Warning, "id.format", $"ID '{id}' should only use lowercase letters, digits and underscores.", c);
            }

            if (name.Length == 0) Add(Severity.Error, "name.empty", $"The category with ID '{id}' has no name.", c);
            else
            {
                if (!names.TryGetValue(name, out var l)) names[name] = l = new List<CraftCategory>();
                l.Add(c);
            }
        }

        foreach (var (id, list) in ids)
            if (list.Count > 1)
                foreach (var c in list) Add(Severity.Error, "id.duplicate", $"ID '{id}' is used by {list.Count} categories.", c);
        foreach (var (name, list) in names)
            if (list.Count > 1)
                foreach (var c in list) Add(Severity.Error, "name.duplicate", $"The name '{name}' is used by {list.Count} categories.", c);

        return issues;
    }
}
