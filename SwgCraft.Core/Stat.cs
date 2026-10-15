namespace SwgCraft.Core;

/// <summary>SWGEmu stat codes. GH's feed uses MA for Malleability; that is translated at the feed boundary only.</summary>
public enum Stat { ER, CR, CD, DR, FL, HR, MB, PE, OQ, SR, UT }

public static class StatInfo
{
    public static readonly IReadOnlyList<Stat> All = Enum.GetValues<Stat>();

    public static string FullName(Stat s) => s switch
    {
        Stat.ER => "Entangle Resistance",
        Stat.CR => "Cold Resistance",
        Stat.CD => "Conductivity",
        Stat.DR => "Decay Resistance",
        Stat.FL => "Flavor",
        Stat.HR => "Heat Resistance",
        Stat.MB => "Malleability",
        Stat.PE => "Potential Energy",
        Stat.OQ => "Overall Quality",
        Stat.SR => "Shock Resistance",
        Stat.UT => "Unit Toughness",
        _ => s.ToString()
    };
}

public static class ExternalSources
{
    public const string Swgemu = "swgemu";
    public const string GalaxyHarvester = "galaxyHarvester";
}
