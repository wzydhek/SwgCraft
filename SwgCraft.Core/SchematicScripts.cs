using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwgCraft.Core;

// ---------------------------------------------------------------- SWGEmu draft schematic scripts (Core3 bin/scripts/object/draft_schematic)

/// <summary>
/// One ingredient slot of a draft schematic script, in the script's order. Slot types (Core3 DraftSchematicObjectTemplate):
/// 0 resource, 1 identical component, 2 mixed component, 3 optional identical, 4 optional mixed.
/// </summary>
public sealed record ScriptSlot(int Index, string TitleFile, string TitleId, int SlotType, string Ingredient, int Quantity)
{
    public bool IsResource => SlotType == 0;
    public bool Optional => SlotType is 3 or 4;
    /// <summary>Identical in our sense: the slot wants identical units and there is more than one.</summary>
    public bool Identical => SlotType is 1 or 3 && Quantity >= 2;
}

/// <summary>What a draft schematic script says about a schematic.</summary>
public sealed class SchematicScript
{
    public string Path { get; init; } = "";
    public string? CustomObjectName { get; init; }
    public int? Complexity { get; init; }
    public int? Size { get; init; }
    public string? TargetTemplate { get; init; }
    public List<ScriptSlot> Slots { get; } = new();
    /// <summary>Lists in the script that don't line up (different lengths); the slots are cut to the shortest.</summary>
    public string? Problem { get; init; }
}

/// <summary>Reads the parts of a Core3 draft schematic .lua file the app uses. Not a Lua interpreter: it reads the plain table literals.</summary>
public static class SchematicScriptReader
{
    static readonly Regex LineComment = new(@"--[^\r\n]*", RegexOptions.Compiled);

    public static SchematicScript ReadFile(string path) => Parse(File.ReadAllText(path), path);

    public static SchematicScript Parse(string text, string path = "")
    {
        text = LineComment.Replace(text, "");
        var files = Strings(text, "ingredientTemplateNames");
        var titles = Strings(text, "ingredientTitleNames");
        var types = Numbers(text, "ingredientSlotType");
        var ingredients = Strings(text, "resourceTypes");
        var amounts = Numbers(text, "resourceQuantities");

        int n = new[] { files.Count, titles.Count, types.Count, ingredients.Count, amounts.Count }.Min();
        int most = new[] { files.Count, titles.Count, types.Count, ingredients.Count, amounts.Count }.Max();
        var script = new SchematicScript
        {
            Path = path,
            CustomObjectName = Scalar(text, "customObjectName"),
            Complexity = int.TryParse(ScalarNumber(text, "complexity"), out var c) ? c : null,
            Size = int.TryParse(ScalarNumber(text, "size"), out var s) ? s : null,
            TargetTemplate = Scalar(text, "targetTemplate"),
            Problem = n == most ? null
                : $"The script's slot lists have different lengths ({files.Count} names files, {titles.Count} names, {types.Count} types, " +
                  $"{ingredients.Count} ingredients, {amounts.Count} amounts); only the first {n} slots are used.",
        };
        for (int i = 0; i < n; i++)
            script.Slots.Add(new ScriptSlot(i, files[i], titles[i], types[i], ingredients[i], amounts[i]));
        return script;
    }

    static string? Body(string text, string field)
    {
        var m = Regex.Match(text, @"\b" + Regex.Escape(field) + @"\s*=\s*\{(.*?)\}", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    static List<string> Strings(string text, string field) =>
        Body(text, field) is { } b ? Regex.Matches(b, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList() : new();

    static List<int> Numbers(string text, string field) =>
        Body(text, field) is { } b ? Regex.Matches(b, @"-?\d+").Select(m => int.Parse(m.Value)).ToList() : new();

    static string? Scalar(string text, string field)
    {
        var m = Regex.Match(text, @"\b" + Regex.Escape(field) + "\\s*=\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string? ScalarNumber(string text, string field)
    {
        var m = Regex.Match(text, @"\b" + Regex.Escape(field) + @"\s*=\s*(-?\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }
}

/// <summary>
/// The .lua files below a draft_schematic folder, findable by schematic ID. A schematic ID is the file's path below the
/// folder joined with '_' - from the full path ("weapon_rifle_disrupter_dxr6") or from any trailing part of it
/// ("armor_segment_zam_advanced" for armor/armor_segment_zam_advanced.lua); IDs that would match two files aren't used.
/// </summary>
public sealed class SchematicScriptIndex
{
    readonly Dictionary<string, List<string>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> _keysOf = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<string>>? _byTarget;

    public string Root { get; }
    public int FileCount { get; }

    public SchematicScriptIndex(string root)
    {
        Root = root;
        foreach (var file in Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories))
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.Equals("objects.lua", StringComparison.OrdinalIgnoreCase) || name.Equals("serverobjects.lua", StringComparison.OrdinalIgnoreCase)) continue;
            FileCount++;
            var parts = System.IO.Path.ChangeExtension(System.IO.Path.GetRelativePath(root, file), null)!
                .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var keys = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                var key = string.Join("_", parts[i..]);
                keys.Add(key);
                if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = new List<string>();
                list.Add(file);
            }
            _keysOf[file] = keys;
        }
    }

    /// <summary>The script file for a schematic ID, or null when there is none or the ID is ambiguous.</summary>
    public string? Find(string schematicId) =>
        _byKey.TryGetValue(schematicId, out var files) && files.Count == 1 ? files[0] : null;

    /// <summary>The IDs a script file can be known by (its full joined path first).</summary>
    public IReadOnlyList<string> KeysOf(string file) => _keysOf.TryGetValue(file, out var k) ? k : Array.Empty<string>();

    /// <summary>Script files whose schematic makes the given object template ("shared_" and case ignored). Reads every file once.</summary>
    public IReadOnlyList<string> ProducersOf(string template)
    {
        if (_byTarget == null)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in _keysOf.Keys)
            {
                string? target;
                try { target = SchematicScriptReader.ReadFile(file).TargetTemplate; }
                catch (IOException) { continue; }
                if (string.IsNullOrWhiteSpace(target)) continue;
                var key = NormalizeTemplate(target);
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
                list.Add(file);
            }
            _byTarget = map;
        }
        return _byTarget.TryGetValue(NormalizeTemplate(template), out var files) ? files : Array.Empty<string>();
    }

    /// <summary>"object/tangible/component/weapon/shared_scope_weapon.iff" -> "object/tangible/component/weapon/scope_weapon.iff".</summary>
    public static string NormalizeTemplate(string template)
    {
        var t = template.Trim().Replace('\\', '/');
        int slash = t.LastIndexOf('/');
        var file = t[(slash + 1)..];
        if (file.StartsWith("shared_", StringComparison.OrdinalIgnoreCase)) file = file["shared_".Length..];
        return (slash >= 0 ? t[..(slash + 1)] : "") + file;
    }
}

/// <summary>What filling a schematic from its script would do.</summary>
public sealed class ScriptMergeResult
{
    /// <summary>The schematic with the changes made (a copy; the original is untouched).</summary>
    public Schematic Merged { get; init; } = new();
    /// <summary>What was filled in or added.</summary>
    public List<string> Changes { get; } = new();
    /// <summary>Where the script disagrees with what is already entered. Nothing is changed for these.</summary>
    public List<string> Differences { get; } = new();
    /// <summary>Things that couldn't be worked out.</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Fills a schematic from its draft schematic script without overwriting anything already entered: blank slot names are filled,
/// slots the schematic doesn't have yet are added, an empty complexity or size is filled. Anything that differs is only reported.
/// Resource slots are matched to the script's resource slots in order, component slots to its component slots in order.
/// </summary>
public static class SchematicScriptMerge
{
    public static ScriptMergeResult Merge(Schematic current, SchematicScript script, StringCatalog strings, SchematicCatalog schematics,
                                          ClassCatalog classes, ResourceTree? tree, SchematicScriptIndex? index)
    {
        var merged = JsonSerializer.Deserialize<Schematic>(JsonSerializer.Serialize(current, JsonFile.Options), JsonFile.Options)!;
        var result = new ScriptMergeResult { Merged = merged };
        if (script.Problem != null) result.Notes.Add(script.Problem);

        string? SlotName(ScriptSlot s)
        {
            var text = strings.Find(s.TitleFile, s.TitleId);
            if (text == null)
            {
                result.Notes.Add($"No string for slot {s.Index + 1} name @{s.TitleFile}:{s.TitleId} (import the client string files to get it).");
                return null;
            }
            text = text.Trim();
            return text.EndsWith('.') ? text[..^1].TrimEnd() : text;
        }

        // ---- resources
        var resSlots = script.Slots.Where(s => s.IsResource).ToList();
        for (int i = 0; i < resSlots.Count; i++)
        {
            var s = resSlots[i];
            var name = SlotName(s);
            if (i < merged.Resources.Count)
            {
                var ours = merged.Resources[i];
                var label = $"Resource slot {i + 1}";
                if (string.IsNullOrWhiteSpace(ours.Name) && name != null)
                {
                    ours.Name = name;
                    result.Changes.Add($"{label}: name \"{name}\"");
                }
                else if (name != null && !string.Equals(ours.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    result.Differences.Add($"{label} name: entered \"{ours.Name}\", script \"{name}\"");
                if (!string.Equals(ours.Resource?.Trim(), s.Ingredient, StringComparison.OrdinalIgnoreCase))
                    result.Differences.Add($"{label} resource: entered {ours.Resource}, script {s.Ingredient}");
                if (ours.Amount != s.Quantity)
                    result.Differences.Add($"{label} amount: entered {ours.Amount}, script {s.Quantity}");
            }
            else
            {
                merged.Resources.Add(new ResourceSlot { Name = name, Resource = s.Ingredient, Amount = s.Quantity });
                result.Changes.Add($"Added resource slot {i + 1}: {(name != null ? $"\"{name}\" " : "")}{s.Ingredient} x {s.Quantity}");
                if (tree != null && tree.Find(s.Ingredient) == null)
                    result.Notes.Add($"Resource slot {i + 1}: '{s.Ingredient}' is not in the resource tree.");
            }
        }
        if (merged.Resources.Count > resSlots.Count)
            result.Differences.Add($"The schematic has {merged.Resources.Count} resource slots, the script {resSlots.Count}.");

        // ---- components
        var compSlots = script.Slots.Where(s => !s.IsResource).ToList();
        for (int i = 0; i < compSlots.Count; i++)
        {
            var s = compSlots[i];
            var name = SlotName(s);
            var producers = ProducerIds(s.Ingredient, schematics, index);
            if (i < merged.Components.Count)
            {
                var ours = merged.Components[i];
                var label = $"Component slot {i + 1}";
                if (string.IsNullOrWhiteSpace(ours.Name) && name != null)
                {
                    ours.Name = name;
                    result.Changes.Add($"{label}: name \"{name}\"");
                }
                else if (name != null && !string.Equals(ours.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    result.Differences.Add($"{label} name: entered \"{ours.Name}\", script \"{name}\"");
                if (ours.Quantity != s.Quantity)
                    result.Differences.Add($"{label} quantity: entered {ours.Quantity}, script {s.Quantity}");
                if (ours.Identical != s.Identical)
                    result.Differences.Add($"{label} identical: entered {(ours.Identical ? "yes" : "no")}, script {(s.Identical ? "yes" : "no")}");
                if (ours.Optional != s.Optional)
                    result.Differences.Add($"{label} optional: entered {(ours.Optional ? "yes" : "no")}, script {(s.Optional ? "yes" : "no")}");
                if (producers.Count > 0 && !Accepts(ours, producers, classes))
                    result.Differences.Add($"{label} component: entered {ours.Kind} '{ours.Target}', the script's slot takes {Template(s)} (made by {string.Join(", ", producers)})");
            }
            else
            {
                var target = Resolve(producers, classes);
                if (target == null)
                {
                    result.Notes.Add($"Component slot {i + 1} {(name != null ? $"\"{name}\" " : "")}takes {Template(s)} x {s.Quantity}, " +
                                     (producers.Count == 0 ? "which no schematic in the scripts makes (looted?)" : $"made by {string.Join(", ", producers)}, but no class holds them") +
                                     ". Not added - add it by hand.");
                    continue;
                }
                merged.Components.Add(new ComponentSlot
                {
                    Name = name, Kind = target.Value.Kind, Target = target.Value.Id, Quantity = s.Quantity, Identical = s.Identical, Optional = s.Optional,
                });
                result.Changes.Add($"Added component slot {i + 1}: {(name != null ? $"\"{name}\" " : "")}{target.Value.Kind} '{target.Value.Id}' x {s.Quantity}" +
                                   (s.Identical ? ", identical" : "") + (s.Optional ? ", optional" : ""));
            }
        }
        if (merged.Components.Count > compSlots.Count)
            result.Differences.Add($"The schematic has {merged.Components.Count} component slots, the script {compSlots.Count}.");

        // ---- complexity and size, only when empty
        if (merged.Complexity == null && script.Complexity is int cx)
        {
            merged.Complexity = cx;
            result.Changes.Add($"Complexity {cx}");
        }
        else if (script.Complexity is int sc && merged.Complexity != sc)
            result.Differences.Add($"Complexity: entered {merged.Complexity}, script {sc}");
        if (merged.Size == null && script.Size is int sz)
        {
            merged.Size = sz;
            result.Changes.Add($"Size {sz}");
        }
        else if (script.Size is int ss && merged.Size != ss)
            result.Differences.Add($"Size: entered {merged.Size}, script {ss}");

        return result;
    }

    static string Template(ScriptSlot s) => System.IO.Path.GetFileNameWithoutExtension(SchematicScriptIndex.NormalizeTemplate(s.Ingredient));

    /// <summary>Our schematic IDs whose script makes the slot's object template.</summary>
    static List<string> ProducerIds(string template, SchematicCatalog schematics, SchematicScriptIndex? index)
    {
        if (index == null) return new();
        return index.ProducersOf(template)
            .Select(f => index.KeysOf(f).FirstOrDefault(k => schematics.Find(k) != null))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The class holding most of the producers (a class slot), else the one producer (a schematic slot).</summary>
    static (SlotKind Kind, string Id)? Resolve(List<string> producers, ClassCatalog classes)
    {
        if (producers.Count == 0) return null;
        var best = classes.All
            .Select(c => (Class: c, Hits: c.Members.Count(m => m.Kind == MemberKind.Schematic && producers.Contains(m.Id))))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits).ThenBy(x => x.Class.Members.Count)
            .FirstOrDefault();
        if (best.Class != null) return (SlotKind.Class, best.Class.Id);
        return producers.Count == 1 ? (SlotKind.Schematic, producers[0]) : null;
    }

    /// <summary>Whether what is entered takes something the script's slot takes.</summary>
    static bool Accepts(ComponentSlot ours, List<string> producers, ClassCatalog classes) => ours.Kind switch
    {
        SlotKind.Schematic => producers.Contains(ours.Target),
        SlotKind.Class => classes.Find(ours.Target) is { } c && c.Members.Any(m => m.Kind == MemberKind.Schematic && producers.Contains(m.Id)),
        _ => true,   // a looted item: can't be checked against the scripts
    };
}
