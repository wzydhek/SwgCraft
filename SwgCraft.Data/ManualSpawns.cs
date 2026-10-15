using SwgCraft.Core;

namespace SwgCraft.Data;

public static class SpawnDates
{
    /// <summary>Stored in spawned_utc for manual entries with no known spawn date (the column is NOT NULL).</summary>
    public static readonly DateTimeOffset Unknown = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static bool IsUnknown(DateTimeOffset t) => t.UtcDateTime <= Unknown.UtcDateTime;
}

public static class SpawnSources
{
    public const string Feed = "feed";
    public const string Manual = "manual";
}

/// <summary>A past spawn entered by hand. Dates are optional; stats are the ones the resource type has.</summary>
public sealed record ManualSpawnInput(
    string Name, string ResourceTypeId, IReadOnlyDictionary<Stat, int> Stats,
    DateTimeOffset? SpawnedUtc, DateTimeOffset? DespawnedUtc);

public sealed record ManualSpawnCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

internal sealed partial class GalaxySpawnStore
{
    static string NormalizeName(string? s) => (s ?? "").Trim().ToLowerInvariant();

    /// <summary>Errors must be fixed; warnings can be confirmed by the user.</summary>
    public ManualSpawnCheck CheckManualSpawn(ManualSpawnInput input, ResourceTree tree, long? excludeSpawnId = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var name = NormalizeName(input.Name);
        if (name.Length == 0 || name.Any(char.IsWhiteSpace)) errors.Add("The name must be a single word.");

        var node = tree.Find(input.ResourceTypeId);
        if (node == null || !node.IsLeaf) errors.Add("Choose a specific resource type (a leaf in the tree).");
        if (input.Stats.Count == 0) errors.Add("Enter at least one stat.");

        foreach (var (stat, value) in input.Stats.OrderBy(k => k.Key))
        {
            if (value < 0 || value > 1000) { errors.Add($"{stat} must be between 0 and 1000."); continue; }
            if (node is { IsLeaf: true, Ranges: { Count: > 0 } ranges })
            {
                if (!ranges.TryGetValue(stat, out var r))
                    errors.Add($"{stat} is not one of the stats {node.Name} has.");
                else if (value < r.Min || value > r.Max)
                    warnings.Add($"{stat}={value} is outside the {node.Name} range {r.Min}-{r.Max}.");
            }
        }

        if (input.SpawnedUtc is { } sp && input.DespawnedUtc is { } dp && dp < sp)
            errors.Add("The despawn date is before the spawn date.");

        if (errors.Count == 0)
        {
            using var c = _db.Open();
            var same = Convert.ToInt32(Sql.Scalar(c, null,
                "SELECT COUNT(*) FROM spawn WHERE galaxy_id = @g AND name = @n AND resource_type_id = @t AND id <> @x",
                ("@g", GalaxyId), ("@n", name), ("@t", node!.Id), ("@x", excludeSpawnId ?? 0L)));
            if (same > 0)
                warnings.Add($"A '{name}' of type {node.Name} is already in the database. Use the picker for that one, " +
                             "unless this is a different spawn that reused the name.");
        }
        return new ManualSpawnCheck(errors, warnings);
    }

    /// <summary>Inserts a past (despawned) spawn flagged as manual. Call <see cref="CheckManualSpawn"/> first.</summary>
    public long AddManualSpawn(ManualSpawnInput input)
    {
        var ps = StatParams(input.Stats);
        ps.Add(("@g", GalaxyId));
        ps.Add(("@n", NormalizeName(input.Name)));
        ps.Add(("@t", input.ResourceTypeId));
        ps.Add(("@sp", DbTime.ToDb(input.SpawnedUtc ?? SpawnDates.Unknown)));
        ps.Add(("@dp", DbTime.ToDb(input.DespawnedUtc ?? DateTimeOffset.UtcNow)));
        ps.Add(("@now", DbTime.ToDb(DateTimeOffset.UtcNow)));

        using var c = _db.Open();
        Sql.Exec(c, null,
            $"INSERT INTO spawn (galaxy_id, name, resource_type_id, {StatColList}, spawned_utc, first_seen_utc, " +
            $"last_seen_utc, despawned_utc, source) VALUES (@g, @n, @t, {StatParamList}, @sp, @now, @now, @dp, 'manual')",
            ps.ToArray());
        return Convert.ToInt64(Sql.Scalar(c, null, "SELECT last_insert_rowid()"));
    }

    /// <summary>Edits a manual spawn. Returns false for feed spawns (the sync owns those) or unknown IDs.</summary>
    public bool UpdateManualSpawn(long spawnId, ManualSpawnInput input)
    {
        var ps = StatParams(input.Stats);
        ps.Add(("@id", spawnId));
        ps.Add(("@g", GalaxyId));
        ps.Add(("@n", NormalizeName(input.Name)));
        ps.Add(("@t", input.ResourceTypeId));
        ps.Add(("@sp", DbTime.ToDb(input.SpawnedUtc ?? SpawnDates.Unknown)));
        ps.Add(("@dp", DbTime.ToDb(input.DespawnedUtc ?? DateTimeOffset.UtcNow)));

        using var c = _db.Open();
        return Sql.Exec(c, null,
            $"UPDATE spawn SET name = @n, resource_type_id = @t, {StatSetList}, spawned_utc = @sp, despawned_utc = @dp " +
            "WHERE id = @id AND galaxy_id = @g AND source = 'manual'", ps.ToArray()) > 0;
    }

    /// <summary>Deletes a manual spawn that no inventory stack refers to. Returns false otherwise.</summary>
    public bool DeleteManualSpawn(long spawnId)
    {
        using var c = _db.Open();
        return Sql.Exec(c, null,
            "DELETE FROM spawn WHERE id = @id AND galaxy_id = @g AND source = 'manual' " +
            "AND NOT EXISTS (SELECT 1 FROM inventory WHERE spawn_id = @id)",
            ("@id", spawnId), ("@g", GalaxyId)) > 0;
    }
}
