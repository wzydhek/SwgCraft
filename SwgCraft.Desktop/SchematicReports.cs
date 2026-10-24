using System.IO;
using System.Text;
using System.Windows;
using SwgCraft.Core;

namespace SwgCraft.Desktop;

/// <summary>
/// The schematic reports of the Reports menu. They read the saved static data (schematics, component classes, looted items)
/// and the SWGEmu scripts; Refresh in a report window reads them again. The script data is read once and kept.
/// </summary>
public static class SchematicReports
{
    static SchematicScriptIndex? _scriptIndex;
    static StringCatalog? _strings;
    static ObjectTemplateIndex? _objectIndex;
    static LootScriptIndex? _lootScripts;
    static string? _objectIndexFolder;

    static string Describe(Schematic s) => $"{(string.IsNullOrWhiteSpace(s.Name) ? "(no name)" : s.Name.Trim())} ({s.Id})";

    static List<Schematic> LoadSchematics() =>
        Services.StaticData.LoadSchematics().Value.Schematics.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();

    // ------------------------------------------------------------ opening

    public static void ShowMissingSlotNames(Window owner) =>
        _ = Open(owner, "Missing Slot Names", "missing slot names.txt", () => Task.FromResult<(string Text, string Summary)?>(ComputeMissingNames()));

    public static void ShowScriptDifferences(Window owner) =>
        _ = Open(owner, "Script Differences", "script differences.txt", () => ComputeScriptDiffAsync(owner));

    public static void ShowSlotCandidates(Window owner) =>
        _ = Open(owner, "Slot Candidates", "slot candidates.txt", () => ComputeSlotCandidatesAsync(owner));

    public static void ShowExperimentals(Window owner) =>
        _ = Open(owner, "Experimentals vs Scripts", "experimentals vs scripts.txt", () => ComputeExperimentalsAsync(owner));

    static async Task Open(Window owner, string title, string fileName, Func<Task<(string Text, string Summary)?>> compute)
    {
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        (string Text, string Summary)? r;
        try { r = await compute(); }
        catch (Exception ex)
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            MessageBox.Show(owner, $"Could not build the report:\n{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        if (r is { } report)
            new BuildStepsWindow(report.Text, report.Summary, fileName, compute) { Owner = owner, Title = title }.Show();
    }

    // ------------------------------------------------------------ the scripts folder

    /// <summary>Asks for the SWGEmu draft_schematic folder and remembers it in the settings. False when cancelled.</summary>
    public static bool ChooseScriptsFolder(Window owner)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the SWGEmu draft_schematic scripts folder (...\\scripts\\object\\draft_schematic)",
            InitialDirectory = Services.Settings.SchematicScriptsFolder is { } f && Directory.Exists(f) ? f : "",
        };
        if (dlg.ShowDialog(owner) != true) return false;
        var folder = dlg.FolderName;
        // pointing at the scripts root (or object) instead of draft_schematic itself is forgiven
        foreach (var sub in new[] { Path.Combine("object", "draft_schematic"), "draft_schematic" })
            if (Directory.Exists(Path.Combine(folder, sub))) { folder = Path.Combine(folder, sub); break; }
        Services.Settings.SchematicScriptsFolder = folder;
        try { SettingsStore.Save(Services.Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "Could not save the settings:\n" + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return true;
    }

    /// <summary>The draft_schematic folder, asking for it when it isn't set or no longer exists; null when cancelled.</summary>
    static string? ScriptsFolder(Window owner)
    {
        var folder = Services.Settings.SchematicScriptsFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) return folder;
        return ChooseScriptsFolder(owner) ? Services.Settings.SchematicScriptsFolder : null;
    }

    /// <summary>scripts/object/draft_schematic -> scripts.</summary>
    static string ScriptsRoot(string draftFolder) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(draftFolder)) ?? draftFolder) ?? draftFolder;

    static SchematicScriptIndex ScriptIndex(string folder)
    {
        var ix = _scriptIndex;
        if (ix != null && string.Equals(ix.Root, folder, StringComparison.OrdinalIgnoreCase)) return ix;
        return _scriptIndex = new SchematicScriptIndex(folder);
    }

    static StringCatalog Strings() => _strings ??= new StringCatalog(Services.StaticData.LoadStrings().Value);

    // ------------------------------------------------------------ missing slot names

    /// <summary>Schematics with a resource or component slot that has no slot name.</summary>
    static (string Text, string Summary) ComputeMissingNames()
    {
        var models = LoadSchematics();
        var rows = models
            .Select(s => (S: s,
                          Res: s.Resources.Select((r, i) => (r, i)).Where(x => string.IsNullOrWhiteSpace(x.r.Name)).Select(x => x.i + 1).ToList(),
                          Comp: s.Components.Select((c, i) => (c, i)).Where(x => string.IsNullOrWhiteSpace(x.c.Name)).Select(x => x.i + 1).ToList()))
            .Where(x => x.Res.Count + x.Comp.Count > 0)
            .OrderBy(x => x.S.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int slots = rows.Sum(x => x.Res.Count + x.Comp.Count);
        var sb = new StringBuilder();
        sb.AppendLine($"SCHEMATICS WITH SLOTS WITHOUT A NAME: {rows.Count:N0} of {models.Count:N0} schematics, {slots:N0} slots");
        sb.AppendLine($"Fully named: {models.Count - rows.Count:N0}. (Schematics with no slots at all count as fully named.) Saved schematics only.");
        sb.AppendLine();
        foreach (var (s, res, comp) in rows)
        {
            var parts = new List<string>();
            if (res.Count > 0) parts.Add((res.Count == s.Resources.Count ? "all resource slots" : "resource slot" + (res.Count > 1 ? "s " : " ") + string.Join(", ", res)) + $" (of {s.Resources.Count})");
            if (comp.Count > 0) parts.Add((comp.Count == s.Components.Count ? "all component slots" : "component slot" + (comp.Count > 1 ? "s " : " ") + string.Join(", ", comp)) + $" (of {s.Components.Count})");
            sb.AppendLine($"{Describe(s)}: {string.Join("; ", parts)}");
        }
        return (sb.ToString(), $"{rows.Count:N0} schematic(s), {slots:N0} slot(s) without a name");
    }

    // ------------------------------------------------------------ script differences

    /// <summary>Every schematic compared with its SWGEmu script: where they disagree, what could be filled in, and which have no script.</summary>
    static async Task<(string Text, string Summary)?> ComputeScriptDiffAsync(Window owner)
    {
        var folder = ScriptsFolder(owner);
        if (folder == null) return null;
        var models = LoadSchematics();
        var all = new SchematicsDocument();
        all.Schematics.AddRange(models);
        var classes = new ClassCatalog(Services.StaticData.LoadClasses().Value);
        var tree = Services.Tree;

        return await Task.Run(() =>
        {
            var index = ScriptIndex(folder);
            var strings = Strings();
            var catalog = new SchematicCatalog(all);

            var noScript = new List<Schematic>();
            var unreadable = new List<string>();
            var differing = new List<(Schematic S, ScriptMergeResult R)>();
            int fillable = 0, matching = 0;
            foreach (var s in models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var file = index.Find(s.Id?.Trim() ?? "");
                if (file == null) { noScript.Add(s); continue; }
                ScriptMergeResult r;
                try { r = SchematicScriptMerge.Merge(s, SchematicScriptReader.ReadFile(file), strings, catalog, classes, tree, index); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable.Add($"{Describe(s)}: {ex.Message}"); continue; }
                if (r.Changes.Count > 0) fillable++;
                // a script whose slot lists don't line up is a disagreement worth seeing too
                var problems = r.Notes.Where(n => n.StartsWith("The script's slot lists", StringComparison.Ordinal)).ToList();
                if (r.Differences.Count + problems.Count > 0) differing.Add((s, r));
                else matching++;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"SCHEMATICS COMPARED WITH THE SWGEMU SCRIPTS IN {folder}");
            sb.AppendLine($"{models.Count:N0} saved schematics: {differing.Count:N0} differ, {matching:N0} agree, {noScript.Count:N0} have no script" +
                          (unreadable.Count > 0 ? $", {unreadable.Count:N0} unreadable" : "") + ".");
            sb.AppendLine($"{fillable:N0} of them have something From Script (in the schematic editor) could fill in (empty slot names, missing slots, empty complexity or size).");
            sb.AppendLine("Nothing has been changed.");
            sb.AppendLine();

            sb.AppendLine($"DIFFERENCES ({differing.Count:N0})");
            foreach (var (s, r) in differing)
            {
                sb.AppendLine(Describe(s) + "  <-  " + Path.GetRelativePath(folder, index.Find(s.Id!.Trim())!));
                foreach (var note in r.Notes.Where(x => x.StartsWith("The script's slot lists", StringComparison.Ordinal))) sb.AppendLine("    ! " + note);
                foreach (var d in r.Differences) sb.AppendLine("    - " + d);
            }
            sb.AppendLine();

            sb.AppendLine($"NO SCRIPT FOUND FOR THE ID ({noScript.Count:N0})");
            foreach (var s in noScript) sb.AppendLine("    " + Describe(s));
            if (unreadable.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"COULD NOT BE READ ({unreadable.Count:N0})");
                foreach (var u in unreadable) sb.AppendLine("    " + u);
            }
            return ((string Text, string Summary)?)(sb.ToString(), $"{differing.Count:N0} differ, {matching:N0} agree, {noScript.Count:N0} without a script");
        });
    }

    // ------------------------------------------------------------ slot candidates

    /// <summary>What fits each kind of component slot (from the object scripts) against the component classes entered.</summary>
    static async Task<(string Text, string Summary)?> ComputeSlotCandidatesAsync(Window owner)
    {
        var folder = ScriptsFolder(owner);
        if (folder == null) return null;
        // the object scripts sit next to draft_schematic: scripts/object/tangible
        var objectFolder = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? folder, "tangible");
        if (!Directory.Exists(objectFolder))
        {
            MessageBox.Show(owner, $"The object scripts were not found at\n{objectFolder}\n\nThe scripts folder should be ...\\scripts\\object\\draft_schematic, " +
                                   "with ...\\scripts\\object\\tangible next to it.", "Slot Candidates", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var models = LoadSchematics();
        var classes = new ClassCatalog(Services.StaticData.LoadClasses().Value);
        var loot = new LootCatalog(Services.StaticData.LoadLootItems().Value);
        var scriptsRoot = ScriptsRoot(folder);

        return await Task.Run(() =>
        {
            var index = ScriptIndex(folder);
            if (_objectIndex == null || _lootScripts == null || !string.Equals(_objectIndexFolder, objectFolder, StringComparison.OrdinalIgnoreCase))
            {
                _objectIndex = new ObjectTemplateIndex(objectFolder);
                _lootScripts = new LootScriptIndex(scriptsRoot);
                _objectIndexFolder = objectFolder;
            }
            return ((string Text, string Summary)?)SlotCandidateReport.Build(models, index, _objectIndex, Strings(), classes, loot, _lootScripts);
        });
    }

    // ------------------------------------------------------------ experimentals

    /// <summary>
    /// Every schematic's experimentals compared with the experimentation data of the object it makes (the draft schematic
    /// script's targetTemplate, e.g. scripts/object/weapon/ranged/carbine/carbine_dxr6.lua).
    /// </summary>
    static async Task<(string Text, string Summary)?> ComputeExperimentalsAsync(Window owner)
    {
        var folder = ScriptsFolder(owner);
        if (folder == null) return null;
        var models = LoadSchematics();
        var scriptsRoot = ScriptsRoot(folder);

        return await Task.Run(() =>
        {
            var index = ScriptIndex(folder);
            var strings = Strings();

            var noScript = new List<Schematic>();
            var noTarget = new List<string>();
            var differing = new List<(Schematic S, string Target, List<string> Lines)>();
            var ignoredDiffering = new List<(Schematic S, string Target, List<string> Lines)>();
            int agree = 0, neither = 0;
            foreach (var s in models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var file = index.Find(s.Id?.Trim() ?? "");
                if (file == null) { noScript.Add(s); continue; }
                string? target;
                try { target = SchematicScriptReader.ReadFile(file).TargetTemplate; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { noTarget.Add($"{Describe(s)}: {ex.Message}"); continue; }
                if (string.IsNullOrWhiteSpace(target)) { noTarget.Add($"{Describe(s)}: the draft schematic script has no targetTemplate"); continue; }

                var rel = Path.ChangeExtension(target.Trim(), ".lua")!;
                var path = Path.Combine(scriptsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { noTarget.Add($"{Describe(s)}: {rel} not found"); continue; }

                List<ScriptModifier> mods;
                try { mods = ExperimentScriptReader.Parse(File.ReadAllText(path), strings) ?? new List<ScriptModifier>(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { noTarget.Add($"{Describe(s)}: {ex.Message}"); continue; }

                if (mods.Count == 0 && s.Experiments.Count == 0) { neither++; continue; }
                var lines = ExperimentComparison.Compare(s, mods);
                if (lines.Count == 0) { agree++; continue; }
                (s.IgnoreExperimentation ? ignoredDiffering : differing).Add((s, rel, lines));
            }

            var sb = new StringBuilder();
            sb.AppendLine("EXPERIMENTALS COMPARED WITH THE SWGEMU OBJECT SCRIPTS");
            sb.AppendLine($"{models.Count:N0} saved schematics: {differing.Count + ignoredDiffering.Count:N0} differ ({ignoredDiffering.Count:N0} of them marked to ignore experimentation), " +
                          $"{agree:N0} agree, {neither:N0} have no experimentation in either, {noScript.Count:N0} have no draft schematic script, {noTarget.Count:N0} have no readable object script.");
            sb.AppendLine("The script's lines come from the crafted object's experimental properties: attributes in the \"null\" group, without stats, or with a fixed value (min = max) are not experimentable and are left out.");
            sb.AppendLine("Stats are shown as whole percentages adding up to 100 (weights 2:1 = 67/33). Names come from the string tables (@crafting, @obj_attr_n) and are matched ignoring case.");
            sb.AppendLine("Nothing has been changed.");
            sb.AppendLine();

            void Section(string title, List<(Schematic S, string Target, List<string> Lines)> list)
            {
                sb.AppendLine($"{title} ({list.Count:N0})");
                foreach (var (s, target, lines) in list)
                {
                    sb.AppendLine($"{Describe(s)}  <-  {target}");
                    foreach (var l in lines) sb.AppendLine("    - " + l);
                }
                sb.AppendLine();
            }
            Section("DIFFERENCES", differing);
            if (ignoredDiffering.Count > 0) Section("DIFFERENCES ON SCHEMATICS MARKED TO IGNORE EXPERIMENTATION", ignoredDiffering);

            if (noTarget.Count > 0)
            {
                sb.AppendLine($"NO READABLE OBJECT SCRIPT ({noTarget.Count:N0})");
                foreach (var n in noTarget) sb.AppendLine("    " + n);
                sb.AppendLine();
            }
            sb.AppendLine($"NO DRAFT SCHEMATIC SCRIPT FOUND FOR THE ID ({noScript.Count:N0})");
            foreach (var s in noScript) sb.AppendLine("    " + Describe(s));
            return ((string Text, string Summary)?)(sb.ToString(), $"{differing.Count + ignoredDiffering.Count:N0} differ, {agree:N0} agree");
        });
    }
}
