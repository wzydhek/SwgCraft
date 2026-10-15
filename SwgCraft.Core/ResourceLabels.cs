namespace SwgCraft.Core;

/// <summary>Short display names for resource-tree nodes: just the node's own name ("Ferrous Metal"), not its whole path.</summary>
public static class ResourceLabels
{
    /// <summary>
    /// Node ID -> label. A name used by more than one node gets just enough of its ancestors added to tell them apart,
    /// e.g. "Steel [Ferrous Metal]" and "Steel [Alloy]". Interior nodes are NOT marked here; callers add "(any)" where it helps.
    /// </summary>
    public static Dictionary<string, string> Build(ResourceTreeDocument doc)
    {
        var nodes = new List<(ResourceNode Node, List<string> Ancestors)>();   // ancestors nearest first
        void Walk(ResourceNode n, List<string> ancestorsNearestFirst)
        {
            nodes.Add((n, ancestorsNearestFirst));
            if (n.Children == null) return;
            var next = new List<string>(ancestorsNearestFirst.Count + 1) { n.Name };
            next.AddRange(ancestorsNearestFirst);
            foreach (var c in n.Children) Walk(c, next);
        }
        foreach (var root in doc.Nodes) Walk(root, new List<string>());

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in nodes.Where(t => !string.IsNullOrEmpty(t.Node.Id)).GroupBy(t => t.Node.Name ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            if (items.Count == 1) { Add(labels, items[0].Node, items[0].Node.Name); continue; }

            int maxDepth = items.Max(t => t.Ancestors.Count);
            int level = 1;
            for (; level < maxDepth; level++)
                if (items.Select(t => Suffix(t.Node.Name, t.Ancestors, level)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count) break;
            foreach (var (node, ancestors) in items) Add(labels, node, Suffix(node.Name, ancestors, level));
        }
        return labels;
    }

    static void Add(Dictionary<string, string> labels, ResourceNode n, string label) => labels.TryAdd(n.Id, label);

    static string Suffix(string name, List<string> ancestorsNearestFirst, int level)
    {
        if (ancestorsNearestFirst.Count == 0) return name;
        var parts = ancestorsNearestFirst.Take(level).Reverse();   // outermost first, reads like the path
        return $"{name} [{string.Join(" > ", parts)}]";
    }
}
