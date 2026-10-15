using System.Text.Json.Serialization;

namespace SwgCraft.Core;

/// <summary>Persisted shape of resources.json.</summary>
public sealed class ResourceTreeDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<ResourceNode> Nodes { get; set; } = new();
}

public sealed class StatRange
{
    public int Min { get; set; }
    public int Max { get; set; }
}

/// <summary>
/// A node in the resource tree. Leaves are spawnable resource types (they carry ranges and external IDs);
/// interior nodes are groupings such as "Mineral".
/// </summary>
public sealed class ResourceNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SortedDictionary<string, string>? ExternalIds { get; set; }
    public SortedDictionary<Stat, StatRange>? Ranges { get; set; }
    public List<ResourceNode>? Children { get; set; }

    [JsonIgnore]
    public bool IsLeaf => Children == null || Children.Count == 0;
}
