using System.Text.RegularExpressions;

namespace SwgCraft.Core;

// ---------------------------------------------------------------- experimentation from the SWGEmu object scripts

/// <summary>
/// One experimental attribute of a crafted object's server script (scripts/object/...): the experimental line it belongs to
/// (experimentalGroupTitles, e.g. "expDamage"), the attribute (experimentalSubGroupTitles, e.g. "mindamage") and its stats
/// as whole percentages adding up to 100.
/// </summary>
/// <param name="ExperimentNameKnown">False when the group's string isn't in the string tables (ExperimentName is then the raw ID).</param>
/// <param name="ModifierNameKnown">False when the subgroup's string isn't in the string tables (ModifierName is then the raw ID,
/// e.g. "cat_skill_mod_bonus.@stat_n:healing_injury_treatment", which the game shows as "Mod One").</param>
public sealed record ScriptModifier(string Group, string SubGroup, string ExperimentName, string ModifierName, SortedDictionary<Stat, int> Weights,
                                    bool ExperimentNameKnown = true, bool ModifierNameKnown = true);

public static class ExperimentScriptReader
{
    static readonly Regex LineComment = new(@"--[^\r\n]*", RegexOptions.Compiled);

    /// <summary>
    /// The experimental attributes of an object script, with names from the string tables (@crafting:group,
    /// @obj_attr_n:subgroup). Attributes in the "null" group or without stats ("XX") are left out - they can't be
    /// experimented on, and so are attributes with a fixed value (experimentalMin equal to experimentalMax, e.g. the
    /// spice boosters' 1000/1000 "hitpoints"), which the game doesn't offer for experimentation. Null when the script has
    /// no experimentation data at all.
    /// </summary>
    public static List<ScriptModifier>? Parse(string text, StringCatalog strings)
    {
        text = LineComment.Replace(text, "");
        var counts = Numbers(text, "numberExperimentalProperties");
        if (counts == null) return null;
        var props = Strings(text, "experimentalProperties") ?? new();
        var weights = Numbers(text, "experimentalWeights") ?? new();
        var groups = Strings(text, "experimentalGroupTitles") ?? new();
        var subs = Strings(text, "experimentalSubGroupTitles") ?? new();
        var mins = Numbers(text, "experimentalMin");
        var maxs = Numbers(text, "experimentalMax");

        var result = new List<ScriptModifier>();
        int at = 0;
        for (int i = 0; i < counts.Count; i++)
        {
            int n = (int)counts[i];
            var stats = new List<(Stat Stat, double Weight)>();
            for (int k = at; k < at + n && k < props.Count; k++)
                if (ToStat(props[k]) is Stat st)
                    stats.Add((st, k < weights.Count ? weights[k] : 1));
            at += n;

            var group = i < groups.Count ? groups[i] : "null";
            var sub = i < subs.Count ? subs[i] : "";
            if (group.Equals("null", StringComparison.OrdinalIgnoreCase) || stats.Count == 0 || stats.Sum(s => s.Weight) <= 0) continue;
            // a fixed value (experimentalMin = experimentalMax) can't be raised: the game shows no experimentation for it
            if (mins != null && maxs != null && i < mins.Count && i < maxs.Count && mins[i] == maxs[i]) continue;
            var expName = strings.Find("crafting", group)?.Trim();
            var modName = strings.Find("obj_attr_n", sub)?.Trim();
            result.Add(new ScriptModifier(group, sub,
                                          string.IsNullOrEmpty(expName) ? group : expName,
                                          string.IsNullOrEmpty(modName) ? sub : modName,
                                          Percentages(stats),
                                          ExperimentNameKnown: !string.IsNullOrEmpty(expName),
                                          ModifierNameKnown: !string.IsNullOrEmpty(modName)));
        }
        return result;
    }

    /// <summary>The script's stat codes; Core3 writes malleability as "MA", the app as MB. "XX" is no stat.</summary>
    public static Stat? ToStat(string code) => code.Trim().ToUpperInvariant() switch
    {
        "MA" => Stat.MB,
        "XX" or "" => null,
        var c => Enum.TryParse<Stat>(c, out var s) ? s : null,
    };

    /// <summary>Weights as whole percentages adding up to exactly 100 (largest remainder), e.g. 2:1 -> 67/33.</summary>
    public static SortedDictionary<Stat, int> Percentages(IReadOnlyList<(Stat Stat, double Weight)> weights)
    {
        var merged = weights.GroupBy(w => w.Stat).Select(g => (Stat: g.Key, Weight: g.Sum(x => x.Weight))).ToList();
        double total = merged.Sum(w => w.Weight);
        var raw = merged.Select(w => (w.Stat, Value: w.Weight * 100 / total)).ToList();
        var result = raw.ToDictionary(r => r.Stat, r => (int)Math.Floor(r.Value));
        int left = 100 - result.Values.Sum();
        foreach (var r in raw.OrderByDescending(r => r.Value - Math.Floor(r.Value)).ThenBy(r => r.Stat).Take(left))
            result[r.Stat]++;
        return new SortedDictionary<Stat, int>(result);
    }

    static string? Body(string text, string field)
    {
        var m = Regex.Match(text, @"\b" + Regex.Escape(field) + @"\s*=\s*\{(.*?)\}", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    static List<string>? Strings(string text, string field) =>
        Body(text, field) is { } b ? Regex.Matches(b, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList() : null;

    static List<double>? Numbers(string text, string field) =>
        Body(text, field) is { } b
            ? Regex.Matches(b, @"-?\d+(?:\.\d+)?").Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList()
            : null;
}

/// <summary>Compares a schematic's experimentals with those of its crafted object's script. Names are matched ignoring case.</summary>
public static class ExperimentComparison
{
    public static string WeightsText(IReadOnlyDictionary<Stat, int> w) =>
        w.Count == 0 ? "(none)" : string.Join(", ", w.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value}"));

    static bool SameWeights(IReadOnlyDictionary<Stat, int> a, IReadOnlyDictionary<Stat, int> b) =>
        a.Count(x => x.Value > 0) == b.Count(x => x.Value > 0) && a.Where(x => x.Value > 0).All(x => b.TryGetValue(x.Key, out var v) && v == x.Value);

    static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The differences, as lines; empty when they agree.</summary>
    public static List<string> Compare(Schematic schematic, IReadOnlyList<ScriptModifier> script)
    {
        var lines = new List<string>();
        var scriptExps = script.GroupBy(m => m.ExperimentName, StringComparer.OrdinalIgnoreCase)
                               .Select(g => (Name: g.Key, Mods: g.ToList())).ToList();
        var entered = schematic.Experiments.ToList();

        // experiments: by name, then a remaining pair that has the same modifiers counts as a renamed experiment
        var pairs = new List<(Experiment Mine, string ScriptName, List<ScriptModifier> Mods)>();
        var leftMine = new List<Experiment>(entered);
        var leftScript = new List<(string Name, List<ScriptModifier> Mods)>();
        foreach (var se in scriptExps)
        {
            var mine = leftMine.FirstOrDefault(e => Same(e.Name, se.Name));
            if (mine != null) { pairs.Add((mine, se.Name, se.Mods)); leftMine.Remove(mine); }
            else leftScript.Add(se);
        }
        // a renamed experiment: the remaining one sharing the most modifier names, or the only one left on each side
        foreach (var se in leftScript.ToList())
        {
            var mine = leftMine
                .Select(e => (E: e, Shared: e.Modifiers.Count(m => se.Mods.Any(sm => Same(m.Name, sm.ModifierName)))))
                .Where(x => x.Shared > 0).OrderByDescending(x => x.Shared).Select(x => x.E).FirstOrDefault()
                ?? (leftScript.Count == 1 && leftMine.Count == 1 ? leftMine[0] : null);
            if (mine == null) continue;
            // a name the string tables don't have can't be compared: whatever is entered is taken as its name
            if (se.Mods.All(m => m.ExperimentNameKnown))
                lines.Add($"Experimental name: entered \"{mine.Name}\", script \"{se.Name}\"");
            pairs.Add((mine, se.Name, se.Mods));
            leftMine.Remove(mine);
            leftScript.Remove(se);
        }
        foreach (var se in leftScript)
            lines.Add($"Experimental \"{se.Name}\" is in the script but not entered: " +
                      string.Join("; ", se.Mods.Select(m => $"{m.ModifierName} ({WeightsText(m.Weights)})")));
        foreach (var mine in leftMine)
            lines.Add($"Experimental \"{mine.Name}\" is entered but not in the script");

        // modifiers within each paired experiment
        foreach (var (mine, scriptName, mods) in pairs)
        {
            var leftMods = new List<ExperimentModifier>(mine.Modifiers);
            var unmatched = new List<ScriptModifier>();
            foreach (var sm in mods)
            {
                var m = leftMods.FirstOrDefault(x => Same(x.Name, sm.ModifierName));
                if (m == null) { unmatched.Add(sm); continue; }
                leftMods.Remove(m);
                if (!SameWeights(m.Weights, sm.Weights))
                    lines.Add($"{mine.Name} / {m.Name}: entered {WeightsText(m.Weights)}, script {WeightsText(sm.Weights)}");
            }
            // a leftover pair (one each, or with the same stats) is a modifier whose name differs; a script modifier whose
            // name isn't in the string tables takes the next leftover in order, and only its stats are compared
            foreach (var sm in unmatched.ToList())
            {
                var m = leftMods.Count == 1 && unmatched.Count == 1 ? leftMods[0]
                      : leftMods.FirstOrDefault(x => SameWeights(x.Weights, sm.Weights))
                        ?? (!sm.ModifierNameKnown ? leftMods.FirstOrDefault() : null);
                if (m == null) continue;
                bool sameStats = SameWeights(m.Weights, sm.Weights);
                if (sm.ModifierNameKnown)
                    lines.Add($"{mine.Name} / modifier name: entered \"{m.Name}\", script \"{sm.ModifierName}\" ({sm.SubGroup})" +
                              (sameStats ? "" : $"; stats entered {WeightsText(m.Weights)}, script {WeightsText(sm.Weights)}"));
                else if (!sameStats)
                    lines.Add($"{mine.Name} / {m.Name}: entered {WeightsText(m.Weights)}, script {WeightsText(sm.Weights)} (script name {sm.SubGroup} has no string)");
                leftMods.Remove(m);
                unmatched.Remove(sm);
            }
            foreach (var sm in unmatched)
                lines.Add($"{mine.Name}: modifier \"{sm.ModifierName}\"{(sm.ModifierNameKnown ? "" : " (no string for this name)")} ({WeightsText(sm.Weights)}) is in the script but not entered");
            foreach (var m in leftMods)
                lines.Add($"{mine.Name}: modifier \"{m.Name}\" is entered but not in the script");
        }
        return lines;
    }
}
