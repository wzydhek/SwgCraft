using System.Text;
using System.Text.RegularExpressions;

namespace SwgCraft.Core;

// ---------------------------------------------------------------- what fits a component slot, from the SWGEmu object scripts

public enum CandidateKind { Schematic, LootItem, NotEntered }

/// <summary>An object that fits a component slot: something one of our schematics makes, one of our looted items, or neither yet.</summary>
/// <param name="How">Where it comes from in the game: "craftable", "craftable (loot schematic X)", "lootable", ...</param>
public sealed record SlotCandidate(string Template, string Id, string? Name, CandidateKind Kind, string How = "")
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";
}

/// <summary>
/// The object templates in the SWGEmu object scripts (object/tangible/**/objects.lua) and what each derives from. A component
/// slot takes any object whose template is the slot's template or derives from it. Templates are kept normalized
/// ("object/tangible/component/weapon/blaster_power_handler.iff": no "shared_").
/// </summary>
public sealed class ObjectTemplateIndex
{
    readonly Dictionary<string, HashSet<string>> _parents = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _nameRefs = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _isParent = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _parents.Count;

    static readonly Regex ClientTemplate = new("clientTemplateFileName\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex Derived = new(@"derivedFromTemplates\s*=\s*\{(.*?)\}", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex ObjectName = new("objectName\\s*=\\s*\"(@[^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex Quoted = new("\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Reads every objects.lua below the folder (normally scripts/object/tangible).</summary>
    public ObjectTemplateIndex(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "objects.lua", SearchOption.AllDirectories))
            Add(File.ReadAllText(file));
    }

    ObjectTemplateIndex() { }

    /// <summary>For tests: an index from script text.</summary>
    public static ObjectTemplateIndex FromText(params string[] texts)
    {
        var ix = new ObjectTemplateIndex();
        foreach (var t in texts) ix.Add(t);
        return ix;
    }

    void Add(string text)
    {
        // one template per "...:new { ... }" block, each closed by an ObjectTemplates:addClientTemplate(...) line
        foreach (var chunk in text.Split("ObjectTemplates:addClientTemplate("))
        {
            var ct = ClientTemplate.Match(chunk);
            if (!ct.Success) continue;
            var key = SchematicScriptIndex.NormalizeTemplate(ct.Groups[1].Value);
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Derived.Match(chunk) is { Success: true } d)
                foreach (Match q in Quoted.Matches(d.Groups[1].Value))
                    parents.Add(SchematicScriptIndex.NormalizeTemplate(q.Groups[1].Value));
            _parents[key] = parents;
            foreach (var p in parents) _isParent.Add(p);
            if (ObjectName.Match(chunk) is { Success: true } n) _nameRefs[key] = n.Groups[1].Value;
        }
    }

    /// <summary>Templates that fit a slot taking this template: itself and everything derived from it.</summary>
    public List<string> Accepting(string slotTemplate)
    {
        var s = SchematicScriptIndex.NormalizeTemplate(slotTemplate);
        return _parents.Where(kv => kv.Key.Equals(s, StringComparison.OrdinalIgnoreCase) || kv.Value.Contains(s))
                       .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when other templates derive from this one (a base like "droid_service_module_base").</summary>
    public bool IsBase(string template) => _isParent.Contains(SchematicScriptIndex.NormalizeTemplate(template));

    /// <summary>The in-game name of the object, looked up in the string tables; null if unknown.</summary>
    public string? NameOf(string template, StringCatalog strings) =>
        _nameRefs.TryGetValue(SchematicScriptIndex.NormalizeTemplate(template), out var r) ? strings.Resolve(r) : null;
}

/// <summary>
/// Where objects come from besides crafting, from the SWGEmu scripts: loot items (scripts/loot/items/**: directObjectTemplate)
/// and loot schematics (scripts/object/tangible/loot/**: targetDraftSchematic - the draft schematic a looted schematic teaches).
/// </summary>
public sealed class LootScriptIndex
{
    readonly Dictionary<string, List<string>> _lootItems = new(StringComparer.OrdinalIgnoreCase);       // object template -> loot item names
    readonly Dictionary<string, List<string>> _teachers = new(StringComparer.OrdinalIgnoreCase);        // draft schematic template -> loot schematic templates

    static readonly Regex DirectTemplate = new("directObjectTemplate\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex TargetDraft = new("targetDraftSchematic\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex AddTemplate = new("ObjectTemplates:addTemplate\\([^,]+,\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    LootScriptIndex() { }

    /// <summary>Reads scripts/loot/items and scripts/object/tangible/loot below the scripts folder (either may be missing).</summary>
    public LootScriptIndex(string scriptsRoot)
    {
        var items = Path.Combine(scriptsRoot, "loot", "items");
        if (Directory.Exists(items))
            foreach (var file in Directory.EnumerateFiles(items, "*.lua", SearchOption.AllDirectories))
                AddLootItem(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
        var lootObjects = Path.Combine(scriptsRoot, "object", "tangible", "loot");
        if (Directory.Exists(lootObjects))
            foreach (var file in Directory.EnumerateFiles(lootObjects, "*.lua", SearchOption.AllDirectories))
                AddLootSchematic(File.ReadAllText(file));
    }

    /// <summary>For tests.</summary>
    public static LootScriptIndex FromText(IEnumerable<(string Name, string Text)> lootItems, IEnumerable<string> lootSchematics)
    {
        var ix = new LootScriptIndex();
        foreach (var (n, t) in lootItems) ix.AddLootItem(t, n);
        foreach (var t in lootSchematics) ix.AddLootSchematic(t);
        return ix;
    }

    void AddLootItem(string text, string name)
    {
        foreach (Match m in DirectTemplate.Matches(text))
        {
            var key = SchematicScriptIndex.NormalizeTemplate(m.Groups[1].Value);
            if (!_lootItems.TryGetValue(key, out var list)) _lootItems[key] = list = new List<string>();
            if (!list.Contains(name)) list.Add(name);
        }
    }

    void AddLootSchematic(string text)
    {
        if (!text.Contains("targetDraftSchematic")) return;
        var target = TargetDraft.Match(text);
        var self = AddTemplate.Match(text);
        if (!target.Success || !self.Success) return;
        var key = SchematicScriptIndex.NormalizeTemplate(target.Groups[1].Value);
        if (!_teachers.TryGetValue(key, out var list)) _teachers[key] = list = new List<string>();
        list.Add(SchematicScriptIndex.NormalizeTemplate(self.Groups[1].Value));
    }

    /// <summary>The loot item scripts that drop this object.</summary>
    public IReadOnlyList<string> LootItemsFor(string template) =>
        _lootItems.TryGetValue(SchematicScriptIndex.NormalizeTemplate(template), out var l) ? l : Array.Empty<string>();

    /// <summary>The loot schematic objects that teach this draft schematic ("object/draft_schematic/item/item_agitator_motor.iff").</summary>
    public IReadOnlyList<string> TeachersOf(string draftSchematicTemplate) =>
        _teachers.TryGetValue(SchematicScriptIndex.NormalizeTemplate(draftSchematicTemplate), out var l) ? l : Array.Empty<string>();
}

/// <summary>
/// For the schematics entered: every kind of component slot that more than one item fits, what fits it (crafted, looted, not
/// entered yet), and how each schematic's slot is set up - so the component classes can be completed.
/// </summary>
public static class SlotCandidateReport
{
    sealed record Use(Schematic Schematic, int SlotIndex, ComponentSlot? Entered, ScriptSlot Script, string? SlotName);

    public static (string Text, string Summary) Build(IReadOnlyList<Schematic> schematics, SchematicScriptIndex scripts, ObjectTemplateIndex objects,
                                                      StringCatalog strings, ClassCatalog classes, LootCatalog loot, LootScriptIndex? lootScripts = null)
    {
        var catalog = new SchematicCatalog(new SchematicsDocument { Schematics = schematics.ToList() });

        // which of our schematics makes each template
        var makers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in schematics)
        {
            var file = scripts.Find(s.Id?.Trim() ?? "");
            if (file == null) continue;
            string? target;
            try { target = SchematicScriptReader.ReadFile(file).TargetTemplate; }
            catch (IOException) { continue; }
            if (string.IsNullOrWhiteSpace(target)) continue;
            var key = SchematicScriptIndex.NormalizeTemplate(target);
            if (!makers.TryGetValue(key, out var list)) makers[key] = list = new List<string>();
            list.Add(s.Id!);
        }

        // every component slot of every schematic, grouped by the template it takes
        var uses = new Dictionary<string, List<Use>>(StringComparer.OrdinalIgnoreCase);
        var noScript = 0;
        foreach (var s in schematics)
        {
            var file = scripts.Find(s.Id?.Trim() ?? "");
            if (file == null) { noScript++; continue; }
            SchematicScript script;
            try { script = SchematicScriptReader.ReadFile(file); }
            catch (IOException) { continue; }
            var comps = script.Slots.Where(x => !x.IsResource).ToList();
            for (int i = 0; i < comps.Count; i++)
            {
                var key = SchematicScriptIndex.NormalizeTemplate(comps[i].Ingredient);
                if (!uses.TryGetValue(key, out var list)) uses[key] = list = new List<Use>();
                var name = strings.Find(comps[i].TitleFile, comps[i].TitleId)?.Trim().TrimEnd('.');
                list.Add(new Use(s, i, i < s.Components.Count ? s.Components[i] : null, comps[i], name));
            }
        }

        string Label(string id) => catalog.Find(id) is { } sc && !string.IsNullOrWhiteSpace(sc.Name) ? $"{sc.Name.Trim()} ({id})" : id;

        // craftable items whose draft schematic isn't entered: (object, draft schematic ID, how it is learned)
        var missingSchematics = new Dictionary<string, (string Object, string Draft, string LearnedFrom)>(StringComparer.OrdinalIgnoreCase);

        // how an object is obtained in the game: crafted (from which draft schematic; profession or loot schematic) and/or looted
        string How(string t)
        {
            var parts = new List<string>();
            foreach (var file in scripts.ProducersOf(t))
            {
                var keys = scripts.KeysOf(file);
                var entered = keys.FirstOrDefault(k => catalog.Find(k) != null);
                var draft = scripts.DraftTemplateOf(file);
                var teachers = lootScripts?.TeachersOf(draft) ?? Array.Empty<string>();
                var learned = teachers.Count > 0
                    ? "loot schematic " + string.Join(", ", teachers.Select(x => objects.NameOf(x, strings) is { Length: > 0 } n ? $"\"{n}\"" : Path.GetFileNameWithoutExtension(x)))
                    : null;
                var text = "craftable" + (learned != null ? $" from {learned}" : "");
                if (entered == null)
                {
                    var id = keys.Count > 0 ? keys[0] : Path.GetFileNameWithoutExtension(file);
                    text += $" - SCHEMATIC NOT ENTERED: {id}";
                    missingSchematics.TryAdd(id, (t, id, learned ?? "a profession (or a script not linked to a skill)"));
                }
                parts.Add(text);
            }
            if (lootScripts != null && lootScripts.LootItemsFor(t).Count > 0) parts.Add("lootable");
            if (parts.Count == 0) parts.Add(lootScripts == null ? "not craftable" : "not craftable or lootable (quest, reward or vendor item?)");
            return string.Join("; ", parts.Distinct());
        }

        // items with an ID but no name anywhere (not in our data, not in the string tables): scripts that aren't in the game
        var ignoredAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? NameFor(string? entered, string t) => !string.IsNullOrWhiteSpace(entered) ? entered.Trim() : objects.NameOf(t, strings)?.Trim() is { Length: > 0 } n ? n : null;

        (List<SlotCandidate> Fits, List<SlotCandidate> Ignored) CandidatesFor(string slotTemplate)
        {
            var result = new List<SlotCandidate>();
            var ignored = new List<SlotCandidate>();
            foreach (var t in objects.Accepting(slotTemplate))
            {
                if (makers.TryGetValue(t, out var ids))
                {
                    foreach (var id in ids)
                    {
                        var name = NameFor(catalog.Find(id)?.Name, t);
                        if (name == null) { ignored.Add(new SlotCandidate(t, id, null, CandidateKind.Schematic)); continue; }
                        result.Add(new SlotCandidate(t, id, name, CandidateKind.Schematic, How(t)));
                    }
                    continue;
                }
                // an abstract base nothing is made as (unless some schematic or loot really produces it)
                if (objects.IsBase(t) && scripts.ProducersOf(t).Count == 0 && (lootScripts?.LootItemsFor(t).Count ?? 0) == 0) continue;
                var fileId = Path.GetFileNameWithoutExtension(t);
                var lootItem = loot.Find(fileId);
                var kind = lootItem != null ? CandidateKind.LootItem : CandidateKind.NotEntered;
                var itemName = NameFor(lootItem?.Name, t);
                if (itemName == null) { ignored.Add(new SlotCandidate(t, fileId, null, kind)); continue; }
                result.Add(new SlotCandidate(t, fileId, itemName, kind, How(t)));
            }
            foreach (var i in ignored) ignoredAll.Add(i.Id);
            return (result, ignored);
        }

        bool Fits(ComponentSlot slot, SlotCandidate c) => slot.Kind switch
        {
            SlotKind.Schematic => c.Kind == CandidateKind.Schematic && c.Id == slot.Target,
            SlotKind.LootItem => c.Kind == CandidateKind.LootItem && c.Id == slot.Target,
            _ => false,
        };

        // per kind of slot
        var groups = new List<(string Template, List<SlotCandidate> Fits, List<Use> Uses, int Problems, string Text, string Short)>();
        foreach (var (template, list) in uses)
        {
            var (cands, ignoredHere) = CandidatesFor(template);
            if (cands.Count < 2) continue;
            var known = cands.Where(c => c.Kind != CandidateKind.NotEntered).ToList();
            int problems = 0;
            var sb = new StringBuilder();

            var slotNames = list.Select(u => u.SlotName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            sb.AppendLine($"SLOT TYPE: {(slotNames.Count > 0 ? string.Join(" / ", slotNames) : Path.GetFileNameWithoutExtension(template))}   [{template}]");
            sb.AppendLine($"  Fits ({cands.Count}):");
            var ordered = cands.OrderBy(c => c.Kind).ThenBy(c => c.Display, StringComparer.OrdinalIgnoreCase).ToList();
            int width = ordered.Max(c => c.Display.Length);
            foreach (var c in ordered)
                sb.AppendLine("    " + c.Kind switch
                {
                    CandidateKind.Schematic => "crafted      ",
                    CandidateKind.LootItem => "looted       ",
                    _ => "NOT ENTERED  ",
                } + c.Display.PadRight(width) + "  " + c.Template + "  [" + c.How + "]");   // the object's script path below scripts/

            // the classes these slots use, and what they miss
            var usedClasses = list.Where(u => u.Entered?.Kind == SlotKind.Class).Select(u => u.Entered!.Target).Distinct(StringComparer.Ordinal).ToList();
            foreach (var cid in usedClasses)
            {
                var cls = classes.Find(cid);
                if (cls == null) { sb.AppendLine($"  Class '{cid}': does not exist"); problems++; continue; }
                var memberKeys = cls.Members.Select(m => (m.Kind == MemberKind.LootItem ? CandidateKind.LootItem : CandidateKind.Schematic, m.Id)).ToHashSet();
                var missing = known.Where(c => !memberKeys.Contains((c.Kind, c.Id))).ToList();
                var extra = cls.Members.Where(m => !known.Any(c => c.Id == m.Id && c.Kind == (m.Kind == MemberKind.LootItem ? CandidateKind.LootItem : CandidateKind.Schematic))
                                                   && !ignoredHere.Any(c => c.Id == m.Id)).ToList();   // an unnamed one that fits isn't wrong, just not needed
                var head = $"  Class {(string.IsNullOrWhiteSpace(cls.Name) ? cls.Id : cls.Name.Trim())} ({cls.Id}): {cls.Members.Count} members";
                if (missing.Count == 0 && extra.Count == 0) { sb.AppendLine(head + " - complete"); continue; }
                problems += missing.Count + extra.Count;
                sb.AppendLine(head);
                if (missing.Count > 0) sb.AppendLine("    missing: " + string.Join("; ", missing.Select(c => c.Display)));
                if (extra.Count > 0) sb.AppendLine("    in the class but not fitting this slot type: " + string.Join("; ", extra.Select(m => m.Id)));
            }
            if (ignoredHere.Count > 0)
                sb.AppendLine($"  Ignored, no name (probably not in the game): {string.Join(", ", ignoredHere.Select(c => c.Id).Distinct())}");
            int notEntered = cands.Count(c => c.Kind == CandidateKind.NotEntered);
            if (notEntered > 0)
            {
                sb.AppendLine($"  {notEntered} fitting item(s) are not entered yet (add them as looted items, or their schematics, to put them in a class).");
                problems += notEntered;
            }

            sb.AppendLine($"  Used by {list.Count} slot(s):");
            foreach (var u in list.OrderBy(u => u.Schematic.Name, StringComparer.OrdinalIgnoreCase))
            {
                var where = $"    {(string.IsNullOrWhiteSpace(u.Schematic.Name) ? u.Schematic.Id : u.Schematic.Name.Trim())} ({u.Schematic.Id}) slot {u.SlotIndex + 1}" +
                            (string.IsNullOrWhiteSpace(u.SlotName) ? "" : $" \"{u.SlotName}\"") + ": ";
                if (u.Entered == null) { sb.AppendLine(where + "NOT ENTERED (the schematic has fewer component slots)"); problems++; continue; }
                switch (u.Entered.Kind)
                {
                    case SlotKind.Class:
                        sb.AppendLine(where + $"class {u.Entered.Target}");
                        break;
                    default:
                        var fits = known.Any(c => Fits(u.Entered, c));
                        sb.AppendLine(where + $"{(u.Entered.Kind == SlotKind.LootItem ? "looted item" : "schematic")} {Label(u.Entered.Target)}" +
                                      (fits ? $" - only 1 of the {cands.Count} that fit; a class would allow the others" : " - DOES NOT FIT this slot type"));
                        problems++;
                        break;
                }
            }
            // the one-line version for a slot type with nothing to do
            var classText = usedClasses.Count == 0 ? "" : "; class " + string.Join(", ", usedClasses) + " complete";
            var shortLine = $"    {(slotNames.Count > 0 ? string.Join(" / ", slotNames) : Path.GetFileNameWithoutExtension(template))}: " +
                            $"{cands.Count} items fit{classText}; {list.Count} slot(s)   [{template}]";
            groups.Add((template, cands, list, problems, sb.ToString(), shortLine));
        }

        var text = new StringBuilder();
        int withProblems = groups.Count(g => g.Problems > 0);
        text.AppendLine("COMPONENT SLOTS THAT MORE THAN ONE ITEM FITS (from the SWGEmu object and draft schematic scripts)");
        text.AppendLine($"{groups.Count:N0} kinds of slot, used by {groups.Sum(g => g.Uses.Count):N0} slots in your schematics; {withProblems:N0} need attention " +
                        "(a class missing items, a fitting item not entered yet, a slot set to a single item, or a slot not entered).");
        text.AppendLine("An item fits a slot when its object template is the slot's template or derives from it. Abstract base templates are left out.");
        if (ignoredAll.Count > 0)
            text.AppendLine($"{ignoredAll.Count:N0} fitting item(s) with an ID but no name (in your data or the string tables) are ignored as not in the game.");
        if (noScript > 0) text.AppendLine($"{noScript:N0} schematic(s) have no script and are not included (see the Script Differences report).");
        text.AppendLine();
        text.AppendLine($"NEEDS ATTENTION ({withProblems:N0})");
        text.AppendLine();
        foreach (var g in groups.Where(g => g.Problems > 0).OrderBy(g => g.Text, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(g.Text);
            text.AppendLine();
        }
        if (withProblems == 0) text.AppendLine("    nothing").AppendLine();

        var complete = groups.Where(g => g.Problems == 0).OrderBy(g => g.Short, StringComparer.OrdinalIgnoreCase).ToList();
        text.AppendLine($"COMPLETE - every fitting item is entered and every slot uses a class that has them all ({complete.Count:N0})");
        foreach (var g in complete) text.AppendLine(g.Short);
        text.AppendLine();

        if (missingSchematics.Count > 0)
        {
            text.AppendLine($"CRAFTABLE ITEMS THAT FIT THESE SLOTS BUT WHOSE DRAFT SCHEMATIC IS NOT ENTERED ({missingSchematics.Count:N0})");
            foreach (var m in missingSchematics.Values.OrderBy(m => m.Draft, StringComparer.OrdinalIgnoreCase))
                text.AppendLine($"    {m.Draft}  makes {m.Object}  - learned from {m.LearnedFrom}");
        }
        return (text.ToString(), $"{groups.Count:N0} kinds of slot, {withProblems:N0} need attention");
    }
}
