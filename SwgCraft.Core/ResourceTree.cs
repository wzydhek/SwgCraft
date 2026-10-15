using System.Diagnostics.CodeAnalysis;

namespace SwgCraft.Core;

/// <summary>Read-only lookup indexes over a resource tree document. Tolerates invalid data (the validator reports problems).</summary>
public sealed class ResourceTree
{
    public static ResourceTree Empty { get; } = new(new ResourceTreeDocument());

    readonly Dictionary<string, ResourceNode> _byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> _parent = new(StringComparer.Ordinal);
    readonly Dictionary<(string Source, string Id), ResourceNode> _byExternal = new();

    public ResourceTreeDocument Document { get; }

    public ResourceTree(ResourceTreeDocument document)
    {
        Document = document;
        foreach (var n in document.Nodes) Index(n, null);
    }

    void Index(ResourceNode n, string? parentId)
    {
        if (!string.IsNullOrEmpty(n.Id))
        {
            _byId.TryAdd(n.Id, n);
            _parent.TryAdd(n.Id, parentId);
        }
        if (n.ExternalIds != null)
            foreach (var (source, value) in n.ExternalIds)
                if (!string.IsNullOrWhiteSpace(value)) _byExternal.TryAdd((source, value), n);
        if (n.Children != null)
            foreach (var c in n.Children) Index(c, n.Id);
    }

    public ResourceNode? Find(string id) => _byId.GetValueOrDefault(id);

    public bool TryResolveExternal(string source, string externalId, [NotNullWhen(true)] out ResourceNode? node)
        => _byExternal.TryGetValue((source, externalId), out node);

    /// <summary>True if <paramref name="id"/> equals or lies below <paramref name="ancestorId"/>.</summary>
    public bool IsDescendantOrSelf(string id, string ancestorId)
    {
        string? cur = id;
        for (int guard = 0; cur != null && guard < 100; guard++)
        {
            if (cur == ancestorId) return true;
            if (!_parent.TryGetValue(cur, out cur)) return false;
        }
        return false;
    }

    public IEnumerable<ResourceNode> Leaves() => Document.Nodes.SelectMany(LeavesOf);

    public IEnumerable<ResourceNode> LeavesUnder(string id) =>
        Find(id) is { } n ? LeavesOf(n) : Enumerable.Empty<ResourceNode>();

    static IEnumerable<ResourceNode> LeavesOf(ResourceNode n)
    {
        if (n.IsLeaf) { yield return n; yield break; }
        foreach (var c in n.Children!)
            foreach (var l in LeavesOf(c)) yield return l;
    }
}
