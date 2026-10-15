using System.Text.RegularExpressions;

namespace SwgCraft.Core;

/// <summary>Persisted shape of professions.json. The order of the list is the order shown in dropdowns.</summary>
public sealed class ProfessionsDocument
{
    /// <summary>2: professions have skills (1: no skills; still loads, as a profession with no skills).</summary>
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<Profession> Professions { get; set; } = new();
}

public sealed class Profession
{
    /// <summary>Stable key that schematics will reference. Don't rename it once schematics use it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The profession's skills, in the order shown in the editor (e.g. Novice Artisan first).</summary>
    public List<ProfessionSkill> Skills { get; set; } = new();
}

/// <summary>One skill of a profession. Its ID starts with the profession's ID: Artisan + "Novice Artisan" -> "artisan_novice_artisan".</summary>
public sealed class ProfessionSkill
{
    /// <summary>Stable key that other data may refer to. Don't rename it once something uses it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The ID a skill gets from its profession's ID and its own name: ("artisan", "Novice Artisan") -> "artisan_novice_artisan".</summary>
    public static string IdFor(string professionId, string skillName)
    {
        var prof = Slug.From(professionId ?? "");
        var skill = Slug.From(skillName ?? "");
        return skill.Length == 0 ? prof + "_" : prof + "_" + skill;
    }
}

/// <summary>Read-only lookup over a professions document.</summary>
public sealed class ProfessionCatalog
{
    public static ProfessionCatalog Empty { get; } = new(new ProfessionsDocument());

    readonly Dictionary<string, Profession> _byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, (Profession Profession, ProfessionSkill Skill)> _skillById = new(StringComparer.Ordinal);

    public ProfessionsDocument Document { get; }
    public IReadOnlyList<Profession> All => Document.Professions;

    public ProfessionCatalog(ProfessionsDocument document)
    {
        Document = document;
        foreach (var p in document.Professions)
        {
            if (!string.IsNullOrEmpty(p.Id)) _byId.TryAdd(p.Id, p);
            foreach (var s in p.Skills)
                if (!string.IsNullOrEmpty(s.Id)) _skillById.TryAdd(s.Id, (p, s));
        }
    }

    public Profession? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>A skill by its ID, with the profession it belongs to; null if there is none.</summary>
    public (Profession Profession, ProfessionSkill Skill)? FindSkill(string id) =>
        _skillById.TryGetValue(id, out var hit) ? hit : null;
}

public static class ProfessionValidator
{
    static readonly Regex IdPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    public static List<ValidationIssue> Validate(ProfessionsDocument doc)
    {
        var issues = new List<ValidationIssue>();
        var ids = new Dictionary<string, List<Profession>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<Profession>>(StringComparer.OrdinalIgnoreCase);

        void Add(Severity s, string code, string msg, Profession p) => issues.Add(new ValidationIssue(s, code, msg, p));

        if (doc.Professions.Count == 0)
            issues.Add(new ValidationIssue(Severity.Warning, "list.empty", "No professions are defined yet."));

        foreach (var p in doc.Professions)
        {
            var id = p.Id?.Trim() ?? "";
            var name = p.Name?.Trim() ?? "";

            if (id.Length == 0) Add(Severity.Error, "id.empty", $"'{(name.Length == 0 ? "(unnamed)" : name)}' has no ID.", p);
            else
            {
                if (!ids.TryGetValue(id, out var l)) ids[id] = l = new List<Profession>();
                l.Add(p);
                if (!IdPattern.IsMatch(id))
                    Add(Severity.Warning, "id.format", $"ID '{id}' should only use lowercase letters, digits and underscores.", p);
            }

            if (name.Length == 0) Add(Severity.Error, "name.empty", $"The profession with ID '{id}' has no name.", p);
            else
            {
                if (!names.TryGetValue(name, out var l)) names[name] = l = new List<Profession>();
                l.Add(p);
            }
        }

        // skills: issues point at the profession that owns them, and the message names the skill
        var skillIds = new Dictionary<string, List<Profession>>(StringComparer.Ordinal);
        foreach (var p in doc.Professions)
        {
            var pid = p.Id?.Trim() ?? "";
            var plabel = string.IsNullOrWhiteSpace(p.Name) ? (pid.Length == 0 ? "(unnamed)" : pid) : p.Name.Trim();
            var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var s in p.Skills)
            {
                n++;
                var sid = s.Id?.Trim() ?? "";
                var sname = s.Name?.Trim() ?? "";
                var where = $"{plabel} skill {n}" + (sname.Length > 0 ? $" ({sname})" : "");

                if (sname.Length == 0) Add(Severity.Error, "skill.name.empty", $"{where} has no name.", p);
                else if (!skillNames.Add(sname)) Add(Severity.Error, "skill.name.duplicate", $"{plabel} has two skills named '{sname}'.", p);

                if (sid.Length == 0) { Add(Severity.Error, "skill.id.empty", $"{where} has no ID.", p); continue; }
                if (!skillIds.TryGetValue(sid, out var owners)) skillIds[sid] = owners = new List<Profession>();
                owners.Add(p);
                if (!IdPattern.IsMatch(sid))
                    Add(Severity.Warning, "skill.id.format", $"{where}: ID '{sid}' should only use lowercase letters, digits and underscores.", p);
                if (pid.Length > 0 && !sid.StartsWith(pid + "_", StringComparison.Ordinal))
                    Add(Severity.Error, "skill.id.prefix", $"{where}: ID '{sid}' must start with '{pid}_' (e.g. {ProfessionSkill.IdFor(pid, sname)}).", p);
                if (sid == pid + "_")
                    Add(Severity.Error, "skill.id.short", $"{where}: ID '{sid}' has nothing after the profession.", p);
            }
        }
        foreach (var (sid, owners) in skillIds)
            if (owners.Count > 1)
                foreach (var p in owners.Distinct()) Add(Severity.Error, "skill.id.duplicate", $"Skill ID '{sid}' is used {owners.Count} times.", p);

        foreach (var (id, list) in ids)
            if (list.Count > 1)
                foreach (var p in list) Add(Severity.Error, "id.duplicate", $"ID '{id}' is used by {list.Count} professions.", p);

        foreach (var (name, list) in names)
            if (list.Count > 1)
                foreach (var p in list) Add(Severity.Error, "name.duplicate", $"The name '{name}' is used by {list.Count} professions.", p);

        return issues;
    }
}
