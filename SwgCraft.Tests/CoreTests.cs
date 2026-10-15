using SwgCraft.Core;

namespace SwgCraft.Tests;

public class CoreTests
{
    static ResourceNode Leaf(string id, string? gh = null, params Stat[] stats)
    {
        var n = new ResourceNode { Id = id, Name = id };
        if (gh != null) n.ExternalIds = new() { [ExternalSources.GalaxyHarvester] = gh };
        if (stats.Length > 0)
        {
            n.Ranges = new();
            foreach (var s in stats) n.Ranges[s] = new StatRange { Min = 1, Max = 1000 };
        }
        return n;
    }

    static ResourceTreeDocument Doc(params ResourceNode[] roots)
    {
        var d = new ResourceTreeDocument();
        d.Nodes.AddRange(roots);
        return d;
    }

    static bool Has(IEnumerable<ValidationIssue> issues, string code, Severity sev) =>
        issues.Any(i => i.Code == code && i.Severity == sev);

    [Fact]
    public void Slug_builds_stable_ids() => Assert.Equal("steel_kiirium", Slug.From("Steel: Kiirium"));

    [Fact]
    public void Clean_leaf_has_no_errors()
    {
        var issues = ResourceTreeValidator.Validate(Doc(Leaf("steel", "steel_gh", Stat.OQ)));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Duplicate_ids_are_errors() =>
        Assert.True(Has(ResourceTreeValidator.Validate(Doc(Leaf("a", "x", Stat.OQ), Leaf("a", "y", Stat.OQ))), "id.duplicate", Severity.Error));

    [Fact]
    public void Ranges_on_interior_node_are_errors()
    {
        var group = new ResourceNode { Id = "g", Name = "g", Children = new() { Leaf("a", "x", Stat.OQ) },
            Ranges = new() { [Stat.OQ] = new StatRange { Min = 1, Max = 5 } } };
        Assert.True(Has(ResourceTreeValidator.Validate(Doc(group)), "ranges.interior", Severity.Error));
    }

    [Fact]
    public void Gh_id_on_interior_node_is_error()
    {
        var group = new ResourceNode { Id = "g", Name = "g", Children = new() { Leaf("a", "x", Stat.OQ) },
            ExternalIds = new() { [ExternalSources.GalaxyHarvester] = "grp" } };
        Assert.True(Has(ResourceTreeValidator.Validate(Doc(group)), "extid.gh.interior", Severity.Error));
    }

    [Fact]
    public void Duplicate_external_ids_are_errors() =>
        Assert.True(Has(ResourceTreeValidator.Validate(Doc(Leaf("a", "same", Stat.OQ), Leaf("b", "same", Stat.OQ))), "extid.duplicate", Severity.Error));

    [Fact]
    public void Min_above_max_is_error()
    {
        var n = Leaf("a", "x", Stat.OQ);
        n.Ranges![Stat.OQ] = new StatRange { Min = 500, Max = 100 };
        Assert.True(Has(ResourceTreeValidator.Validate(Doc(n)), "ranges.order", Severity.Error));
    }

    [Fact]
    public void Leaf_without_gh_id_is_only_a_warning() =>
        Assert.True(Has(ResourceTreeValidator.Validate(Doc(Leaf("a", null, Stat.OQ))), "extid.gh.missing", Severity.Warning));

    [Fact]
    public void Tree_lookups_work()
    {
        var group = new ResourceNode { Id = "mineral", Name = "Mineral", Children = new() { Leaf("steel", "steel_gh", Stat.OQ) } };
        var tree = new ResourceTree(Doc(new ResourceNode { Id = "inorganic", Name = "Inorganic", Children = new() { group } }));
        Assert.True(tree.IsDescendantOrSelf("steel", "inorganic"));
        Assert.False(tree.IsDescendantOrSelf("inorganic", "steel"));
        Assert.True(tree.TryResolveExternal(ExternalSources.GalaxyHarvester, "steel_gh", out var node));
        Assert.Equal("steel", node!.Id);
        Assert.Single(tree.LeavesUnder("mineral"));
    }

    [Fact]
    public void Json_round_trip_keeps_data_and_rejects_unknown_stat_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swgcraft-{Guid.NewGuid():N}.json");
        try
        {
            var doc = Doc(Leaf("steel", "steel_gh", Stat.OQ, Stat.UT));
            JsonFile.WriteAtomic(path, doc);
            var back = JsonFile.Read<ResourceTreeDocument>(path);
            Assert.Equal(2, back.Nodes[0].Ranges!.Count);
            Assert.Equal("steel_gh", back.Nodes[0].ExternalIds![ExternalSources.GalaxyHarvester]);

            File.WriteAllText(path, """{"schemaVersion":1,"nodes":[{"id":"a","name":"a","ranges":{"ZZ":{"min":1,"max":2}}}]}""");
            Assert.ThrowsAny<Exception>(() => JsonFile.Read<ResourceTreeDocument>(path));
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }
}
