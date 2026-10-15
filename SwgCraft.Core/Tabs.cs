using System.Text.RegularExpressions;

namespace SwgCraft.Core;

/// <summary>Persisted shape of tabs.json: the SWGEmu crafting-window tabs that schematics are listed under.</summary>
public sealed class TabsDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CraftTab> Tabs { get; set; } = new();
}

public sealed class CraftTab
{
    /// <summary>Stable key that schematics refer to (their "tab" field). Don't rename it once schematics use it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Read-only lookup over a tabs document.</summary>
public sealed class TabCatalog
{
    public static TabCatalog Empty { get; } = new(new TabsDocument());

    readonly Dictionary<string, CraftTab> _byId = new(StringComparer.Ordinal);

    public TabsDocument Document { get; }
    public IReadOnlyList<CraftTab> All => Document.Tabs;

    public TabCatalog(TabsDocument document)
    {
        Document = document;
        foreach (var t in document.Tabs)
            if (!string.IsNullOrEmpty(t.Id)) _byId.TryAdd(t.Id, t);
    }

    public CraftTab? Find(string id) => _byId.GetValueOrDefault(id);
}

public static class TabValidator
{
    static readonly Regex IdPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    public static List<ValidationIssue> Validate(TabsDocument doc)
    {
        var issues = new List<ValidationIssue>();
        var ids = new Dictionary<string, List<CraftTab>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<CraftTab>>(StringComparer.OrdinalIgnoreCase);

        void Add(Severity s, string code, string msg, CraftTab t) => issues.Add(new ValidationIssue(s, code, msg, t));

        if (doc.Tabs.Count == 0)
            issues.Add(new ValidationIssue(Severity.Warning, "list.empty", "No tabs are defined yet."));

        foreach (var t in doc.Tabs)
        {
            var id = t.Id?.Trim() ?? "";
            var name = t.Name?.Trim() ?? "";

            if (id.Length == 0) Add(Severity.Error, "id.empty", $"'{(name.Length == 0 ? "(unnamed)" : name)}' has no ID.", t);
            else
            {
                if (!ids.TryGetValue(id, out var l)) ids[id] = l = new List<CraftTab>();
                l.Add(t);
                if (!IdPattern.IsMatch(id))
                    Add(Severity.Warning, "id.format", $"ID '{id}' should only use lowercase letters, digits and underscores.", t);
            }

            if (name.Length == 0) Add(Severity.Error, "name.empty", $"The tab with ID '{id}' has no name.", t);
            else
            {
                if (!names.TryGetValue(name, out var l)) names[name] = l = new List<CraftTab>();
                l.Add(t);
            }
        }

        foreach (var (id, list) in ids)
            if (list.Count > 1)
                foreach (var t in list) Add(Severity.Error, "id.duplicate", $"ID '{id}' is used by {list.Count} tabs.", t);
        foreach (var (name, list) in names)
            if (list.Count > 1)
                foreach (var t in list) Add(Severity.Error, "name.duplicate", $"The name '{name}' is used by {list.Count} tabs.", t);

        return issues;
    }
}
