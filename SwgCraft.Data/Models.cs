using SwgCraft.Core;

namespace SwgCraft.Data;

public sealed record Galaxy(int Id, string Name, string Source)
{
    public string Label => $"{Name} ({Id})";
}

public sealed record Spawn(
    long Id, int GalaxyId, string Name, string ResourceTypeId,
    IReadOnlyDictionary<Stat, int> Stats,
    DateTimeOffset SpawnedUtc, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc,
   DateTimeOffset? DespawnedUtc, string Planets, string Source = "feed",
   bool Filtered = false, bool Generic = false);
public sealed record SyncOptions(int MinFeedPercentOfPrevious = 50, bool AllowLargeDrop = false);

public sealed record SyncResult(
    string Status, int Added, int Updated, int Reopened, int Replaced, int Despawned,
    int Unmapped, int Rejected, int OutOfRange, IReadOnlyList<string> Messages);

public sealed record SyncLogEntry(
    DateTimeOffset FinishedUtc, string Status, int Added, int Updated, int Reopened, int Replaced,
    int Despawned, int Unmapped, int Rejected, string? Message);

/// <summary>One resource record from a GH feed, already translated into app terms (MB, not MA).</summary>
public sealed record FeedSpawn(
    string Name, int GalaxyId, string GalaxyName, DateTimeOffset EnterDate,
    string TypeExternalId, string TypeDisplayName,
    IReadOnlyDictionary<Stat, int> Stats, IReadOnlyList<string> Planets);
