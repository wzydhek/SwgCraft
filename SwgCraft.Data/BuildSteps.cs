using System.Text;

namespace SwgCraft.Data;

/// <summary>One step of the build sequence: a hand-crafting batch or one factory run of one build item.</summary>
public sealed record BuildStep(int Number, BuildItem Item, int Units, bool Factory, int RunIndex, int RunCount, string Title, IReadOnlyList<string> Lines);

/// <summary>
/// Turns a build plan into a numbered, step-by-step sequence: every item after all of its parts, one step per item crafted
/// by hand (all units together) and one per factory run, each listing the spawns to use, the components to put in and where
/// they come from (item inventory or earlier steps), and any shortage. The final item is the last step.
/// </summary>
public static class BuildSteps
{
    public static List<BuildStep> From(BuildPlan plan, Func<string, string> resourceLabel, Func<ItemStack, string> itemName)
    {
        var steps = new List<BuildStep>();
        var stepsOf = new Dictionary<BuildItem, List<int>>();

        // plan.Items lists users before their parts; reversed, every part comes first
        foreach (var item in plan.Items.AsEnumerable().Reverse())
        {
            if (item.ItemKind != BuildItemKind.Schematic || item.CraftCount == 0 || item.Schematic == null) continue;

            var numbers = new List<int>();
            stepsOf[item] = numbers;
            string name = item.Label + (item.IsTarget ? " (the final item)" : "");

            if (!item.FactoryRun)
            {
                int n = steps.Count + 1;
                numbers.Add(n);
                steps.Add(new BuildStep(n, item, item.CraftCount, false, 0, 0, $"Craft {item.CraftCount:N0} x {name} by hand",
                                        Lines(item, item.CraftCount, runIndex: -1, factory: false, last: true)));
                continue;
            }

            bool unmade = item.UnmadeInRuns > 0;
            for (int r = 0; r < item.Runs.Count; r++)
            {
                int n = steps.Count + 1;
                numbers.Add(n);
                var run = item.Runs[r];
                string runs = item.Runs.Count > 1 ? $" (run {r + 1} of {item.Runs.Count})" : "";
                steps.Add(new BuildStep(n, item, run.Size, true, r, item.Runs.Count,
                                        $"Factory run: {run.Size:N0} x {name}{runs}",
                                        Lines(item, run.Size, r, factory: true, last: r == item.Runs.Count - 1 && item.HandUnits == 0 && !unmade)));
            }
            if (item.HandUnits > 0)
            {
                int n = steps.Count + 1;
                numbers.Add(n);
                steps.Add(new BuildStep(n, item, item.HandUnits, false, item.Runs.Count, item.Runs.Count,
                                        $"Craft {item.HandUnits:N0} x {name} by hand" +
                                        (item.Runs.Count > 0 ? " (a factory run of one would waste a unit's worth on its manufacturing schematic)" : " (the stock is too split for a factory run of 2 or more)"),
                                        Lines(item, item.HandUnits, runIndex: -1, factory: false, last: !unmade)));
            }
            if (unmade)
            {
                int n = steps.Count + 1;
                numbers.Add(n);
                int rest = item.UnmadeInRuns;
                steps.Add(new BuildStep(n, item, rest, true, item.Runs.Count, item.Runs.Count,
                                        $"Factory run of {rest:N0} x {name}: NOT POSSIBLE with the stock held",
                                        Lines(item, rest, runIndex: -1, factory: true, last: true)));
            }
        }
        return steps;

        string Sources(BuildItem c)
        {
            var sources = new List<string>();
            if (c.FromInventory.Count > 0)
                sources.Add("item inventory (" + string.Join(", ", c.FromInventory.Select(t =>
                    $"{t.Quantity:N0} x {itemName(t.Stack)}{(t.Stack.Serial != null ? $" serial {t.Stack.Serial}" : "")}")) + ")");
            var made = c.ItemKind == BuildItemKind.Class && c.Recipe != null ? c.Recipe : c;
            if (made.CraftCount > 0 && stepsOf.TryGetValue(made, out var nums) && nums.Count > 0)
                sources.Add((nums.Count == 1 ? "step " : "steps ") + string.Join(", ", nums) +
                            (made != c ? $" ({made.Label})" : ""));
            if (c.MissingCount > 0) sources.Add($"MISSING {c.MissingCount:N0}");
            return sources.Count > 0 ? " - from " + string.Join(" + ", sources) : "";
        }

        // where one identical group comes from: a serial stack, or the step of the run that makes it
        string GroupSource(BuildItem child, BuildDemand d)
        {
            if (d.FilledFrom is { } stack)
                return $"item inventory: {itemName(stack)}{(stack.Serial != null ? $" serial {stack.Serial}" : "")}";
            var made = d.ForwardedAs ?? d;
            var maker = d.ForwardedAs != null && child.Recipe != null ? child.Recipe : child;
            if (made.MadeInRun is int run && stepsOf.TryGetValue(maker, out var nums) && run < nums.Count)
                return $"step {nums[run]}" + (maker != child ? $" ({maker.Label})" : "");
            if (child.ItemKind == BuildItemKind.LootItem || child.MissingCount > 0) return "MISSING (needs one serial stack holding them all)";
            return stepsOf.TryGetValue(maker, out var all) && all.Count > 0 ? $"step {all[^1]} (NOT POSSIBLE)" : "NOT POSSIBLE";
        }

        static string SpecificNote(ResourceTake t) =>
            t.Specific ? " [a type other schematics want specifically - nothing general-purpose is held]" : "";

        List<string> Lines(BuildItem item, int units, int runIndex, bool factory, bool last)
        {
            var lines = new List<string>();
            var used = new List<ResourceTake>();   // what this step takes, for the summary
            if (factory)
            {
                lines.Add($"First craft one {item.Label} with the components and resources below to make the manufacturing schematic " +
                          $"(it uses up one unit's worth), then run {units:N0} in a factory - every unit uses the same spawns and identical components.");
                if (item.FactoryReason != null) lines.Add($"Factory run because {item.FactoryReason}.");
                var groups = runIndex >= 0 && runIndex < item.Runs.Count ? item.Runs[runIndex].Groups : Array.Empty<BuildDemand>();
                if (groups.Count > 0)
                    lines.Add("This run makes identical sets for: " + string.Join("; ", groups.Select(g =>
                        $"{g.Units:N0} for {g.From.Label}{(g.ParentRun >= 0 && g.From.Runs.Count + (g.From.UnmadeInRuns > 0 ? 1 : 0) > 1 ? $" run {g.ParentRun + 1}" : "")}")) +
                        " - keep each set together.");
            }

            foreach (var r in item.Resources)
            {
                var head = $"{resourceLabel(r.ResourceId)}, {r.AmountEach:N0} per unit";
                if (factory)
                {
                    if (runIndex >= 0 && runIndex < r.Takes.Count)
                    {
                        var t = r.Takes[runIndex];
                        used.Add(t);
                        lines.Add($"Resource {head}: {t.Stack.Spawn.Name} - {t.Quantity:N0} units " +
                                  $"({units * r.AmountEach:N0} for the run + {r.AmountEach:N0} for the manufacturing schematic){SpecificNote(t)}");
                    }
                    else if (runIndex < 0)
                        lines.Add($"Resource {head}: {(units + 1) * r.AmountEach:N0} units from one spawn - none held has enough");
                }
                else
                {
                    // hand crafts come after a factory item's run takes
                    var takes = r.Takes.Skip(item.FactoryRun ? item.Runs.Count : 0).ToList();
                    used.AddRange(takes);
                    if (takes.Count == 0)
                        lines.Add($"Resource {head}: none held");
                    else
                        lines.Add($"Resource {head}: " + string.Join("; ", takes.Select(t =>
                            $"{t.Stack.Spawn.Name} - {t.Quantity:N0} units ({t.Crafts:N0} unit{(t.Crafts == 1 ? "" : "s")}){SpecificNote(t)}")));
                }
                if (last && (r.ShortCrafts > 0 || r.ShortSetups > 0))
                    lines.Add($"   SHORT: {r.ShortAmount:N0} units of {resourceLabel(r.ResourceId)} ({r.ShortCrafts:N0} unit(s) can't be made)");
            }

            // one line per spawn, all slots added up
            var totals = used.GroupBy(t => t.Stack.Id)
                .Select(g => (Stack: g.First().Stack, Units: g.Sum(t => t.Quantity)))
                .OrderBy(x => resourceLabel(x.Stack.Spawn.ResourceTypeId), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Stack.Spawn.Name)
                .ToList();
            if (totals.Count > 0)
            {
                lines.Add("Resources for this step:");
                foreach (var (stack, total) in totals)
                    lines.Add($"   {total,10:N0}  {stack.Spawn.Name} ({resourceLabel(stack.Spawn.ResourceTypeId)})");
            }

            if (factory)
            {
                // each slot: one identical set for this run, made separately
                int ri = runIndex >= 0 ? runIndex : item.Runs.Count;
                foreach (var slot in item.Slots)
                {
                    var d = slot.Child.Demands.FirstOrDefault(x => x.From == item && x.ParentRun == ri && x.SlotIndex == slot.SlotIndex);
                    int total = slot.PerUnit * (units + 1);
                    lines.Add($"Component {slot.Child.Label} (slot {slot.SlotIndex + 1}): {slot.PerUnit} per unit - {slot.PerUnit * units:N0} for the run " +
                              $"+ {slot.PerUnit} for the manufacturing schematic = {total:N0}, all identical" +
                              (d != null ? " - from " + GroupSource(slot.Child, d) : ""));
                }
            }
            else
                foreach (var c in item.Components)
                    lines.Add($"Component {c.Child.Label}: {c.PerUnit} per unit, {c.PerUnit * units:N0} here" +
                              (c.Child.Demands.Any(d => d.From == item && d.ParentRun == -1 && d.Identical) ? " (identical sets)" : "") +
                              Sources(c.Child));
            foreach (var o in item.SkippedOptional) lines.Add($"Optional component {o}: not used");

            if (last && item.Problem != null) lines.Add("Note: " + item.Problem);
            return lines;
        }
    }

    /// <summary>The whole report as plain text, for the report window, copying and saving.</summary>
    public static string ToText(BuildPlan plan, IReadOnlyList<BuildStep> steps, Func<string, string> resourceLabel, Func<ItemStack, string> itemName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BUILD STEPS: {plan.Target.Needed:N0} x {plan.Target.Label}");
        sb.AppendLine(plan.Complete ? "Everything needed is on hand." : "WARNING: something is short or missing - see the steps marked SHORT / MISSING.");
        sb.AppendLine($"Resource cost {plan.KnownCost:N1}{(plan.CostIsComplete ? "" : " (some stacks have no price)")}");
        sb.AppendLine();

        var pulls = plan.ResourceSlots.SelectMany(r => r.Takes).GroupBy(t => t.Stack.Id)
            .Select(g => (Stack: g.First().Stack, Units: g.Sum(t => t.Quantity)))
            .OrderBy(x => resourceLabel(x.Stack.Spawn.ResourceTypeId), StringComparer.OrdinalIgnoreCase).ToList();
        var items = plan.Items.SelectMany(n => n.FromInventory).GroupBy(t => t.Stack.Id)
            .Select(g => (Stack: g.First().Stack, Units: g.Sum(t => t.Quantity))).ToList();
        if (pulls.Count + items.Count > 0)
        {
            sb.AppendLine("GATHER FIRST");
            foreach (var (stack, units) in pulls)
                sb.AppendLine($"  {units,10:N0}  {stack.Spawn.Name} ({resourceLabel(stack.Spawn.ResourceTypeId)}) of {stack.Quantity:N0} held");
            foreach (var (stack, units) in items)
                sb.AppendLine($"  {units,10:N0}  {itemName(stack)}{(stack.Serial != null ? $" serial {stack.Serial}" : "")} of {stack.Quantity:N0} held");
            sb.AppendLine();
        }

        foreach (var step in steps)
        {
            sb.AppendLine($"STEP {step.Number}. {step.Title}");
            foreach (var line in step.Lines) sb.AppendLine("    " + line);
            sb.AppendLine();
        }
        if (steps.Count == 0) sb.AppendLine("Nothing needs to be crafted.");
        return sb.ToString();
    }
}
