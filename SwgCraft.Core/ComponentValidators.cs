using System.Text.RegularExpressions;

namespace SwgCraft.Core;

static class IdRules
{
    public static readonly Regex Pattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);
}

public static class LootItemValidator
{
    public static List<ValidationIssue> Validate(LootItemsDocument doc)
    {
        var issues = new List<ValidationIssue>();
        var ids = new Dictionary<string, List<LootItem>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<LootItem>>(StringComparer.OrdinalIgnoreCase);

        void Add(Severity s, string code, string msg, LootItem i) => issues.Add(new ValidationIssue(s, code, msg, i));

        if (doc.Items.Count == 0)
            issues.Add(new ValidationIssue(Severity.Warning, "list.empty", "No looted items are defined yet."));

        foreach (var i in doc.Items)
        {
            var id = i.Id?.Trim() ?? "";
            var name = i.Name?.Trim() ?? "";
            if (id.Length == 0) Add(Severity.Error, "id.empty", $"'{(name.Length == 0 ? "(unnamed)" : name)}' has no ID.", i);
            else
            {
                if (!ids.TryGetValue(id, out var l)) ids[id] = l = new List<LootItem>();
                l.Add(i);
                if (!IdRules.Pattern.IsMatch(id))
                    Add(Severity.Warning, "id.format", $"ID '{id}' should only use lowercase letters, digits and underscores.", i);
            }
            if (name.Length == 0) Add(Severity.Error, "name.empty", $"The looted item with ID '{id}' has no name.", i);
            else
            {
                if (!names.TryGetValue(name, out var l)) names[name] = l = new List<LootItem>();
                l.Add(i);
            }
        }

        foreach (var (id, list) in ids)
            if (list.Count > 1)
                foreach (var i in list) Add(Severity.Error, "id.duplicate", $"ID '{id}' is used by {list.Count} looted items.", i);
        foreach (var (name, list) in names)
            if (list.Count > 1)
                foreach (var i in list) Add(Severity.Error, "name.duplicate", $"The name '{name}' is used by {list.Count} looted items.", i);

        return issues;
    }
}

public static class ClassValidator
{
    /// <summary>Pass null for <paramref name="schematics"/> or <paramref name="loot"/> to skip checking members of that kind.</summary>
    public static List<ValidationIssue> Validate(ComponentClassesDocument doc, SchematicsDocument? schematics, LootItemsDocument? loot)
    {
        var issues = new List<ValidationIssue>();
        var ids = new Dictionary<string, List<ComponentClass>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<ComponentClass>>(StringComparer.OrdinalIgnoreCase);
        var schematicIds = schematics?.Schematics.Select(s => s.Id).Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.Ordinal);
        var lootIds = loot?.Items.Select(s => s.Id).Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.Ordinal);

        void Add(Severity s, string code, string msg, ComponentClass c) => issues.Add(new ValidationIssue(s, code, msg, c));

        if (doc.Classes.Count == 0)
            issues.Add(new ValidationIssue(Severity.Warning, "list.empty", "No component classes are defined yet."));

        foreach (var c in doc.Classes)
        {
            var id = c.Id?.Trim() ?? "";
            var name = c.Name?.Trim() ?? "";
            var label = name.Length == 0 ? (id.Length == 0 ? "(unnamed)" : id) : name;

            if (id.Length == 0) Add(Severity.Error, "id.empty", $"Class '{label}' has no ID.", c);
            else
            {
                if (!ids.TryGetValue(id, out var l)) ids[id] = l = new List<ComponentClass>();
                l.Add(c);
                if (!IdRules.Pattern.IsMatch(id))
                    Add(Severity.Warning, "id.format", $"ID '{id}' should only use lowercase letters, digits and underscores.", c);
            }
            if (name.Length == 0) Add(Severity.Error, "name.empty", $"The class with ID '{id}' has no name.", c);
            else
            {
                if (!names.TryGetValue(name, out var l)) names[name] = l = new List<ComponentClass>();
                l.Add(c);
            }

            if (c.Members.Count == 0)
                Add(Severity.Warning, "members.empty", $"Class '{label}' has no members.", c);

            var seen = new HashSet<(MemberKind, string)>();
            foreach (var m in c.Members)
            {
                var mid = m.Id?.Trim() ?? "";
                if (mid.Length == 0) { Add(Severity.Error, "member.empty", $"Class '{label}' has a member with nothing chosen.", c); continue; }
                if (!seen.Add((m.Kind, mid)))
                    Add(Severity.Warning, "member.duplicate", $"Class '{label}' lists {m.Kind} '{mid}' more than once.", c);
                if (m.Kind == MemberKind.Schematic && schematicIds != null && !schematicIds.Contains(mid))
                    Add(Severity.Error, "member.schematic.unknown", $"Class '{label}': schematic '{mid}' does not exist.", c);
                if (m.Kind == MemberKind.LootItem && lootIds != null && !lootIds.Contains(mid))
                    Add(Severity.Error, "member.loot.unknown", $"Class '{label}': looted item '{mid}' does not exist.", c);
            }
        }

        foreach (var (id, list) in ids)
            if (list.Count > 1)
                foreach (var c in list) Add(Severity.Error, "id.duplicate", $"ID '{id}' is used by {list.Count} classes.", c);
        foreach (var (name, list) in names)
            if (list.Count > 1)
                foreach (var c in list) Add(Severity.Error, "name.duplicate", $"The name '{name}' is used by {list.Count} classes.", c);

        return issues;
    }
}

public static class SchematicValidator
{
    /// <param name="categories">Pass null to skip checking the category field.</param>
    /// <param name="tabs">Pass null to skip checking the tab field.</param>
    /// <param name="loot">Pass null to skip checking components that are looted items.</param>
    public static List<ValidationIssue> Validate(SchematicsDocument doc, ProfessionCatalog professions, ComponentClassesDocument classes, ResourceTree tree,
                                                 CategoryCatalog? categories = null, TabCatalog? tabs = null, LootItemsDocument? loot = null)
    {
        var issues = new List<ValidationIssue>();
        var ids = new Dictionary<string, List<Schematic>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<Schematic>>(StringComparer.OrdinalIgnoreCase);
        var schematicIds = doc.Schematics.Select(s => s.Id).Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.Ordinal);
        var classIds = classes.Classes.Select(c => c.Id).Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.Ordinal);
        var lootIds = loot?.Items.Select(i => i.Id).Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.Ordinal);

        void Add(Severity sev, string code, string msg, Schematic s) => issues.Add(new ValidationIssue(sev, code, msg, s));

        foreach (var s in doc.Schematics)
        {
            var id = s.Id?.Trim() ?? "";
            var name = s.Name?.Trim() ?? "";
            var label = name.Length == 0 ? (id.Length == 0 ? "(unnamed)" : id) : name;

            if (id.Length == 0) Add(Severity.Error, "id.empty", $"Schematic '{label}' has no ID.", s);
            else
            {
                if (!ids.TryGetValue(id, out var l)) ids[id] = l = new List<Schematic>();
                l.Add(s);
                if (!IdRules.Pattern.IsMatch(id))
                    Add(Severity.Warning, "id.format", $"ID '{id}' should only use lowercase letters, digits and underscores.", s);
            }
            if (name.Length == 0) Add(Severity.Error, "name.empty", $"The schematic with ID '{id}' has no name.", s);
            else
            {
                if (!names.TryGetValue(name, out var l)) names[name] = l = new List<Schematic>();
                l.Add(s);
            }

            // professions
            if (s.Professions.Count == 0)
                Add(Severity.Error, "prof.none", $"'{label}' has no profession.", s);
            var seenProf = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in s.Professions)
            {
                if (string.IsNullOrWhiteSpace(p)) { Add(Severity.Error, "prof.empty", $"'{label}' has an empty profession entry.", s); continue; }
                if (!seenProf.Add(p)) Add(Severity.Warning, "prof.duplicate", $"'{label}' lists profession '{p}' more than once.", s);
                if (professions.Find(p) == null) Add(Severity.Error, "prof.unknown", $"'{label}': profession '{p}' does not exist.", s);
            }

            // skills (a missing skill is only a warning for now, while skills are being filled in)
            if (s.Skills.Count == 0)
                Add(Severity.Warning, "skill.none", $"'{label}' has no skill yet.", s);
            var seenSkill = new HashSet<string>(StringComparer.Ordinal);
            var profsWithSkill = new HashSet<string>(StringComparer.Ordinal);   // professions that at least one chosen skill belongs to
            foreach (var sk in s.Skills)
            {
                var skid = sk?.Trim() ?? "";
                if (skid.Length == 0) { Add(Severity.Error, "skill.empty", $"'{label}' has an empty skill entry.", s); continue; }
                if (!seenSkill.Add(skid)) { Add(Severity.Warning, "skill.duplicate", $"'{label}' lists skill '{skid}' more than once.", s); continue; }
                if (professions.FindSkill(skid) is not { } hit)
                {
                    Add(Severity.Error, "skill.unknown", $"'{label}': skill '{skid}' does not exist.", s);
                    continue;
                }
                profsWithSkill.Add(hit.Profession.Id);
                if (!s.Professions.Contains(hit.Profession.Id, StringComparer.Ordinal))
                    Add(Severity.Error, "skill.profession",
                        $"'{label}': skill '{hit.Skill.Name}' belongs to {hit.Profession.Name}, which is not ticked under professions.", s);
            }

            // A ticked profession should have at least one of its skills chosen. Skipped while the schematic has no skills at all
            // (skill.none already says so), and for professions that have no skills defined yet (nothing could be chosen).
            if (s.Skills.Count > 0)
                foreach (var pid in s.Professions.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).Distinct(StringComparer.Ordinal))
                    if (professions.Find(pid) is { Skills.Count: > 0 } prof && !profsWithSkill.Contains(pid))
                        Add(Severity.Warning, "prof.noskill",
                            $"'{label}': {(string.IsNullOrWhiteSpace(prof.Name) ? pid : prof.Name)} is ticked, but none of its skills is chosen.", s);

            var cat = s.Category?.Trim() ?? "";
            if (categories != null && cat.Length > 0 && categories.Find(cat) == null)
                Add(Severity.Error, "category.unknown", $"'{label}': category '{cat}' is not in the category list (Static Data > Categories).", s);

            var tab = s.Tab?.Trim() ?? "";
            if (tabs != null && tab.Length > 0 && tabs.Find(tab) == null)
                Add(Severity.Error, "tab.unknown", $"'{label}': tab '{tab}' is not in the tab list (Static Data > Tabs).", s);

            if (s.Complexity is < 0) Add(Severity.Error, "complexity.negative", $"'{label}': complexity is negative.", s);
            if (s.Size is < 0) Add(Severity.Error, "size.negative", $"'{label}': schematic size is negative.", s);

            // component slots
            int n = 0;
            foreach (var slot in s.Components)
            {
                n++;
                var target = slot.Target?.Trim() ?? "";
                var where = $"'{label}' component {n}";
                if (target.Length == 0) Add(Severity.Error, "slot.empty", $"{where} has nothing chosen.", s);
                else if (slot.Kind == SlotKind.Schematic && !schematicIds.Contains(target))
                    Add(Severity.Error, "slot.schematic.unknown", $"{where}: schematic '{target}' does not exist.", s);
                else if (slot.Kind == SlotKind.LootItem && lootIds != null && !lootIds.Contains(target))
                    Add(Severity.Error, "slot.loot.unknown", $"{where}: looted item '{target}' does not exist.", s);
                else if (slot.Kind == SlotKind.Class && !classIds.Contains(target))
                    Add(Severity.Error, "slot.class.unknown", $"{where}: class '{target}' does not exist.", s);

                if (slot.Quantity < 1) Add(Severity.Error, "slot.quantity", $"{where}: quantity must be at least 1.", s);
                if (slot.Identical && slot.Quantity < 2)
                    Add(Severity.Error, "slot.identical.quantity", $"{where}: 'identical' only applies when the quantity is 2 or more.", s);
            }

            // resource slots
            n = 0;
            foreach (var r in s.Resources)
            {
                n++;
                var rid = r.Resource?.Trim() ?? "";
                var where = $"'{label}' resource {n}";
                if (rid.Length == 0) Add(Severity.Error, "res.empty", $"{where} has nothing chosen.", s);
                else
                {
                    if (tree.Find(rid) == null) Add(Severity.Error, "res.unknown", $"{where}: resource '{rid}' is not in the resource tree.", s);
                }
                if (r.Amount < 1) Add(Severity.Error, "res.amount", $"{where}: amount must be at least 1.", s);
            }

            // experimentation: experimentals, each raising one or more modifiers with their own percentages
            n = 0;
            var seenExp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in s.Experiments)
            {
                n++;
                var ename = e.Name?.Trim() ?? "";
                var where = $"'{label}' experimental {n}" + (ename.Length > 0 ? $" ({ename})" : "");
                if (ename.Length == 0) Add(Severity.Error, "exp.name", $"{where} has no name.", s);
                else if (!seenExp.Add(ename)) Add(Severity.Warning, "exp.duplicate", $"'{label}' has two experimentals named '{ename}'.", s);

                if (e.LegacyWeights != null)
                    Add(Severity.Error, "exp.legacy", $"{where} has both old-style percentages (\"weights\") and modifiers; move the percentages into a modifier.", s);
                if (e.Modifiers.Count == 0)
                    Add(Severity.Error, "exp.modifiers", $"{where} has no modifiers.", s);

                int m = 0;
                var seenMod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in e.Modifiers)
                {
                    m++;
                    var mname = mod.Name?.Trim() ?? "";
                    var mwhere = $"{where} modifier {m}" + (mname.Length > 0 ? $" ({mname})" : "");
                    if (mname.Length == 0) Add(Severity.Error, "mod.name", $"{mwhere} has no name.", s);
                    else if (!seenMod.Add(mname)) Add(Severity.Warning, "mod.duplicate", $"{where} has two modifiers named '{mname}'.", s);

                    bool badWeight = false;
                    foreach (var (stat, w) in mod.Weights)
                        if (w < 1 || w > 100)
                        {
                            badWeight = true;
                            Add(Severity.Error, "mod.weight", $"{mwhere}: {stat} is {w}; each percentage must be between 1 and 100 (leave unused stats out).", s);
                        }
                    int sum = mod.Weights.Values.Sum();
                    if (!badWeight && sum != 100)
                        Add(Severity.Error, "mod.sum", $"{mwhere}: percentages add up to {sum}, not 100.", s);
                }
            }
        }

        foreach (var (id, list) in ids)
            if (list.Count > 1)
                foreach (var s in list) Add(Severity.Error, "id.duplicate", $"ID '{id}' is used by {list.Count} schematics.", s);
        foreach (var (name, list) in names)
            if (list.Count > 1)
                foreach (var s in list) Add(Severity.Warning, "name.duplicate", $"The name '{name}' is used by {list.Count} schematics.", s);

        CheckCycles(doc, classes, issues);
        return issues;
    }

    /// <summary>
    /// A schematic that needs itself through direct schematic slots can never be built (error).
    /// If the loop only exists through a class (e.g. the upgraded version needs the base version of the same class),
    /// it is usually fine because the planner can pick another member, so that is only a warning.
    /// </summary>
    static void CheckCycles(SchematicsDocument doc, ComponentClassesDocument classes, List<ValidationIssue> issues)
    {
        var bySchematic = new Dictionary<string, Schematic>(StringComparer.Ordinal);
        foreach (var s in doc.Schematics) if (!string.IsNullOrEmpty(s.Id)) bySchematic.TryAdd(s.Id, s);
        var byClass = new Dictionary<string, ComponentClass>(StringComparer.Ordinal);
        foreach (var c in classes.Classes) if (!string.IsNullOrEmpty(c.Id)) byClass.TryAdd(c.Id, c);

        var edges = new Dictionary<string, List<(string To, bool Soft)>>(StringComparer.Ordinal);
        foreach (var (id, s) in bySchematic)
        {
            var list = new List<(string, bool)>();
            foreach (var slot in s.Components)
            {
                if (slot.Kind == SlotKind.Schematic)
                {
                    if (bySchematic.ContainsKey(slot.Target)) list.Add((slot.Target, false));
                }
                else if (slot.Kind == SlotKind.Class && byClass.TryGetValue(slot.Target, out var cls))   // looted items never lead anywhere
                {
                    foreach (var m in cls.Members)
                        if (m.Kind == MemberKind.Schematic && bySchematic.ContainsKey(m.Id)) list.Add((m.Id, true));
                }
            }
            edges[id] = list;
        }

        bool Reaches(string start, bool hardOnly)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(start);
            bool first = true;
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!first && cur == start) return true;
                if (!first && !seen.Add(cur)) continue;
                first = false;
                foreach (var (to, soft) in edges[cur])
                {
                    if (hardOnly && soft) continue;
                    if (to == start) return true;
                    if (!seen.Contains(to)) stack.Push(to);
                }
            }
            return false;
        }

        foreach (var (id, s) in bySchematic)
        {
            var label = string.IsNullOrWhiteSpace(s.Name) ? id : s.Name;
            if (Reaches(id, true))
                issues.Add(new ValidationIssue(Severity.Error, "cycle", $"'{label}' needs itself through its components, so it can never be built.", s));
            else if (Reaches(id, false))
                issues.Add(new ValidationIssue(Severity.Warning, "cycle.class", $"'{label}' needs itself only through a component class; the planner must pick a different class member.", s));
        }
    }
}
