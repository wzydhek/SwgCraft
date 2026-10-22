using System.Reflection;

namespace SwgCraft.Core;

public static class AppPaths
{
    static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwgCraft");
    public static string SettingsFile => Path.Combine(AppData, "settings.json");
    public static string DefaultDatabaseFile => Path.Combine(AppData, "swgcraft.db");
    public static string FeedCacheFolder => Path.Combine(AppData, "feedcache");
    /// <summary>The static JSON as shipped: the "data" folder next to the exe (copied there by the build).</summary>
    public static string ShippedStaticDataFolder => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>The user's own copy of the static JSON, next to the database: %AppData%\SwgCraft\data.</summary>
    public static string UserStaticDataFolder => Path.Combine(AppData, "data");

    /// <summary>
    /// Debug builds of the desktop app use the project's own data folder (SwgCraft.Desktop\data, written into the exe by
    /// SwgCraft.Desktop.csproj), so edits made while developing land in the source tree and survive Clean/Rebuild.
    /// Release builds use the user's copy in %AppData%\SwgCraft\data, seeded from the shipped files (see <see cref="StaticDataSeeder"/>).
    /// </summary>
    public static string DefaultStaticDataFolder => SourceDataFolder() ?? UserStaticDataFolder;

    public static bool IsUserStaticDataFolder(string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(UserStaticDataFolder)),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the static JSON used to live by default. A settings.json that still names this folder is treated as "use the default".</summary>
    public static string LegacyStaticDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SwgCraft", "data");

    public const string SourceDataFolderKey = "SwgCraftSourceDataFolder";

    static string? SourceDataFolder()
    {
        var path = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
                           .FirstOrDefault(a => a.Key == SourceDataFolderKey)?.Value;
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// The static data folder to use: the default unless settings.json names another one (a relative path is taken from the exe's folder).
    /// The old Documents default counts as "not set".
    /// </summary>
    public static string ResolveStaticDataFolder(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || IsLegacyStaticDataFolder(configured)) return DefaultStaticDataFolder;
        return Path.GetFullPath(configured, AppContext.BaseDirectory);
    }

    public static bool IsLegacyStaticDataFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(LegacyStaticDataFolder)),
                      StringComparison.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Optional override; null means the default (see <see cref="AppPaths.ResolveStaticDataFolder"/>). Not written when null.</summary>
    public string? StaticDataFolder { get; set; }
    public string DatabaseFile { get; set; } = AppPaths.DefaultDatabaseFile;
    public int? ActiveGalaxyId { get; set; }
    public string FeedUrlTemplate { get; set; } = "https://galaxyharvester.net/exports/current{galaxyId}.xml";
    public int MinSyncIntervalMinutes { get; set; } = 30;
    /// <summary>A feed smaller than this percentage of the currently active spawns aborts the sync.</summary>
    public int MinFeedPercentOfPrevious { get; set; } = 50;
    public PriceSettings Pricing { get; set; } = new();
    public List<string> DisabledProfessions { get; set; } = new();
    /// <summary>The draft_schematic folder of the SWGEmu (Core3) scripts, for filling schematics from them. Not written when null.</summary>
    public string? SchematicScriptsFolder { get; set; }
    public bool IsProfessionEnabled(string professionId) =>
        !(DisabledProfessions?.Contains(professionId) ?? false);
}

public static class SettingsStore
{
    public static AppSettings Load(out string? error)
    {
        error = null;
        if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
        try { return JsonFile.Read<AppSettings>(AppPaths.SettingsFile); }
        catch (Exception ex)
        {
            error = $"Could not read settings ({ex.Message}); using defaults.";
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings) => JsonFile.WriteAtomic(AppPaths.SettingsFile, settings);
}
