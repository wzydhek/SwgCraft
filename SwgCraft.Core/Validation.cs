using System.Text.RegularExpressions;

namespace SwgCraft.Core;

public enum Severity { Warning, Error }

/// <summary>Subject is the model object the issue refers to (UI maps it back to a view-model).</summary>
public sealed record ValidationIssue(Severity Severity, string Code, string Message, object? Subject = null);

public static class ResourceTreeValidator
{
    static readonly Regex IdPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    public static List<ValidationIssue> Validate(ResourceTreeDocument doc)
    {
        var issues = new List<ValidationIssue>();
        var byId = new Dictionary<string, List<ResourceNode>>(StringComparer.Ordinal);
        var external = new Dictionary<(string Source, string Id), ResourceNode>();

        void Add(Severity s, string code, string msg, ResourceNode n) => issues.Add(new ValidationIssue(s, code, msg, n));

        void Visit(ResourceNode n, string path)
        {
            var label = string.IsNullOrWhiteSpace(n.Name) ? "(unnamed)" : n.Name;
            var here = path.Length == 0 ? label : $"{path} > {label}";
            bool leaf = n.IsLeaf;

            if (string.IsNullOrWhiteSpace(n.Id))
                Add(Severity.Error, "id.empty", $"'{here}' has no ID.", n);
            else
            {
                if (!byId.TryGetValue(n.Id, out var list)) byId[n.Id] = list = new List<ResourceNode>();
                list.Add(n);
                if (!IdPattern.IsMatch(n.Id))
                    Add(Severity.Warning, "id.format", $"ID '{n.Id}' ({here}) should only use lowercase letters, digits and underscores.", n);
            }
            if (string.IsNullOrWhiteSpace(n.Name))
                Add(Severity.Error, "name.empty", $"Node with ID '{n.Id}' has no name.", n);

            if (!leaf && n.Ranges is { Count: > 0 })
                Add(Severity.Error, "ranges.interior", $"'{here}' has children, so it cannot have stat ranges.", n);

            if (leaf)
            {
                if (n.Ranges == null || n.Ranges.Count == 0)
                    Add(Severity.Warning, "ranges.missing", $"'{here}' has no stat ranges yet.", n);
                else
                    foreach (var (stat, r) in n.Ranges)
                    {
                        if (r.Min < 0) Add(Severity.Error, "ranges.negative", $"'{here}': {stat} minimum is negative.", n);
                        if (r.Max < r.Min) Add(Severity.Error, "ranges.order", $"'{here}': {stat} maximum ({r.Max}) is below minimum ({r.Min}).", n);
                        if (r.Max > 1000) Add(Severity.Warning, "ranges.high", $"'{here}': {stat} maximum {r.Max} is above 1000.", n);
                    }
            }

            if (n.ExternalIds != null)
                foreach (var (source, value) in n.ExternalIds)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        Add(Severity.Error, "extid.empty", $"'{here}': external ID for '{source}' is empty.", n);
                        continue;
                    }
                    if (source == ExternalSources.GalaxyHarvester && !leaf)
                        Add(Severity.Error, "extid.gh.interior", $"'{here}' has children but carries a Galaxy Harvester ID.", n);
                    if (!external.TryAdd((source, value), n))
                        Add(Severity.Error, "extid.duplicate", $"'{here}': {source} ID '{value}' is already used by '{external[(source, value)].Name}'.", n);
                }

            if (leaf && (n.ExternalIds == null || !n.ExternalIds.ContainsKey(ExternalSources.GalaxyHarvester)))
                Add(Severity.Warning, "extid.gh.missing", $"'{here}' has no Galaxy Harvester ID (spawns of this type cannot be matched).", n);

            if (n.Children != null)
                foreach (var c in n.Children) Visit(c, here);
        }

        foreach (var root in doc.Nodes) Visit(root, "");

        foreach (var (id, nodes) in byId)
            if (nodes.Count > 1)
                foreach (var n in nodes)
                    Add(Severity.Error, "id.duplicate", $"ID '{id}' is used by {nodes.Count} nodes.", n);

        return issues;
    }
}
