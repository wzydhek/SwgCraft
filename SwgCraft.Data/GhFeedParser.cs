using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SwgCraft.Core;

namespace SwgCraft.Data;

public static class GalaxyHarvesterStats
{
    /// <summary>GH stat element name -> app stat. GH calls Malleability "MA"; SWGEmu and this app use "MB".</summary>
    public static readonly IReadOnlyDictionary<string, Stat> Map = new Dictionary<string, Stat>
    {
        ["ER"] = Stat.ER, ["CR"] = Stat.CR, ["CD"] = Stat.CD, ["DR"] = Stat.DR,
        ["FL"] = Stat.FL, ["HR"] = Stat.HR, ["MA"] = Stat.MB, ["PE"] = Stat.PE,
        ["OQ"] = Stat.OQ, ["SR"] = Stat.SR, ["UT"] = Stat.UT,
    };
}

public sealed class FeedParseResult
{
    public List<FeedSpawn> Spawns { get; } = new();
    /// <summary>Names of records that were present but unusable. They still count as "seen" so they are not despawned.</summary>
    public HashSet<string> RejectedNames { get; } = new(StringComparer.Ordinal);
    public List<string> Messages { get; } = new();
    public string GalaxyName { get; set; } = "";
    public string? FatalError { get; set; }
}

public static class GhFeedParser
{
    public static FeedParseResult Parse(string xml, int expectedGalaxyId)
    {
        var res = new FeedParseResult();
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException ex)
        {
            res.FatalError = "Feed is not valid XML: " + ex.Message;
            return res;
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (var el in doc.Descendants("resource"))
        {
            count++;
            var name = ((string?)el.Element("name") ?? "").Trim().ToLowerInvariant();

            var g = el.Element("galaxy");
            if (g == null || !int.TryParse((string?)g.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid))
            {
                Reject(res, name, "missing or invalid <galaxy id>");
                continue;
            }
            if (gid != expectedGalaxyId)
            {
                res.FatalError = $"Feed contains galaxy {gid} but galaxy {expectedGalaxyId} was requested. Nothing was imported.";
                return res;
            }
            if (res.GalaxyName.Length == 0) res.GalaxyName = g.Value.Trim();

            var (spawn, error) = ParseRecord(el, name, gid, g.Value.Trim());
            if (spawn == null) { Reject(res, name, error ?? "unknown error"); continue; }

            if (!seenNames.Add(spawn.Name))
            {
                res.Messages.Add($"Duplicate '{spawn.Name}' in feed - using the first occurrence.");
                continue;
            }
            res.Spawns.Add(spawn);
        }

        if (count == 0) res.Messages.Add("Feed contained no <resource> elements.");
        return res;
    }

    static void Reject(FeedParseResult res, string name, string reason)
    {
        res.Messages.Add($"Rejected '{name}': {reason}");
        if (name.Length > 0) res.RejectedNames.Add(name);
    }

    static (FeedSpawn?, string?) ParseRecord(XElement el, string name, int galaxyId, string galaxyName)
    {
        if (name.Length == 0 || name.Any(char.IsWhiteSpace)) return (null, "name is empty or not a single word");

        var typeEl = el.Element("resource_type");
        var typeId = ((string?)typeEl?.Attribute("id") ?? "").Trim();
        if (typeId.Length == 0) return (null, "missing resource_type id");
        var typeName = (typeEl?.Value ?? "").Trim();

        if (!Rfc822.TryParse((string?)el.Element("enter_date"), out var enter))
            return (null, $"unreadable enter_date '{(string?)el.Element("enter_date")}'");

        var statsEl = el.Element("stats");
        if (statsEl == null) return (null, "missing <stats>");
        var stats = new Dictionary<Stat, int>();
        foreach (var s in statsEl.Elements())
        {
            var code = s.Name.LocalName.Trim().ToUpperInvariant();
            if (!GalaxyHarvesterStats.Map.TryGetValue(code, out var stat)) return (null, $"unknown stat '{code}'");
            if (!int.TryParse(s.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return (null, $"stat {code} is not a number");
            if (!stats.TryAdd(stat, v)) return (null, $"stat {code} listed twice");
        }
        if (stats.Count == 0) return (null, "no stats");

        var planets = el.Element("planets")?.Elements("planet")
                          .Select(p => p.Value.Trim()).Where(p => p.Length > 0)
                          .Distinct(StringComparer.Ordinal).ToList()
                      ?? new List<string>();

        return (new FeedSpawn(name, galaxyId, galaxyName, enter, typeId, typeName, stats, planets), null);
    }
}

/// <summary>Parses dates like "Sun, 27 Sep 2026 04:21:31 -0800" without relying on culture-specific zone parsing.</summary>
public static class Rfc822
{
    static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    static readonly Regex Rx = new(
        @"^(?:[A-Za-z]{3},\s*)?(\d{1,2})\s+([A-Za-z]{3})\s+(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(Z|UT|UTC|GMT|[+-]\d{4})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParse(string? text, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var m = Rx.Match(s);
        if (m.Success)
        {
            var ci = CultureInfo.InvariantCulture;
            int month = Array.FindIndex(Months, x => x.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) + 1;
            if (month == 0) return false;
            var zone = m.Groups[7].Value;
            var offset = TimeSpan.Zero;
            if (zone.Length == 5)
            {
                var ts = new TimeSpan(int.Parse(zone.Substring(1, 2), ci), int.Parse(zone.Substring(3, 2), ci), 0);
                offset = zone[0] == '-' ? -ts : ts;
            }
            try
            {
                result = new DateTimeOffset(
                    int.Parse(m.Groups[3].Value, ci), month, int.Parse(m.Groups[1].Value, ci),
                    int.Parse(m.Groups[4].Value, ci), int.Parse(m.Groups[5].Value, ci),
                    m.Groups[6].Success ? int.Parse(m.Groups[6].Value, ci) : 0, offset);
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
    }
}
