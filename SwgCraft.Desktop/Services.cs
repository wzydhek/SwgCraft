using System.Net.Http;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

/// <summary>Tiny composition root. Everything the windows share lives here.</summary>
public static class Services
{
    public static AppSettings Settings { get; private set; } = new();
    public static StaticDataStore StaticData { get; private set; } = null!;
    public static SpawnDatabase Db { get; private set; } = null!;
    public static SpawnStore Spawns { get; private set; } = null!;
    public static FeedSyncService Feed { get; private set; } = null!;
    public static ResourceTree Tree { get; private set; } = ResourceTree.Empty;
    public static string? ResourceLoadError { get; private set; }
    public static List<string> StartupMessages { get; } = new();
    public static ProfessionCatalog Professions { get; private set; } = ProfessionCatalog.Empty;
    public static string? ProfessionLoadError { get; private set; }
    public static event Action? ProfessionsChanged;
    public static event Action? TreeChanged;
    public static event Action? ActiveGalaxyChanged;
    public static event Action? SpawnDataChanged;

    public static void Initialize()
    {
        Settings = SettingsStore.Load(out var settingsError);
        Settings.Pricing ??= new PriceSettings();
        var priceProblem = Settings.Pricing.Check();
        if (priceProblem != null)
            StartupMessages.Add($"Pricing settings are invalid ({priceProblem}). Prices will be left blank until settings.json is fixed.");
        UnitPricing.Calculator = new JnfUnitPriceCalculator(() => Settings.Pricing);
        if (settingsError != null) StartupMessages.Add(settingsError);

        if (AppPaths.IsLegacyStaticDataFolder(Settings.StaticDataFolder))
            Settings.StaticDataFolder = null;   // the old Documents default: switch to the data folder next to the exe
        var staticFolder = AppPaths.ResolveStaticDataFolder(Settings.StaticDataFolder);
        if (AppPaths.IsUserStaticDataFolder(staticFolder))   // release build: make sure the user's copy exists and is current
            StartupMessages.AddRange(StaticDataSeeder.Seed(AppPaths.ShippedStaticDataFolder, staticFolder));
        StaticData = new StaticDataStore(staticFolder);
        Db = new SpawnDatabase(Settings.DatabaseFile);
        Db.Migrate();
        Spawns = new SpawnStore(Db);

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SwgCraftPlanner/0.1 (personal crafting planner)");
        Feed = new FeedSyncService(Spawns, http, () => Settings);

        ReloadTree();
        ReloadProfessions();
    }

    public static void ReloadProfessions()
    {
        var load = StaticData.LoadProfessions();
        ProfessionLoadError = load.Error;
        Professions = new ProfessionCatalog(load.Value);
        ProfessionsChanged?.Invoke();
    }

    public static void SetProfessions(ProfessionsDocument doc)
    {
        Professions = new ProfessionCatalog(doc);
        ProfessionLoadError = null;
        ProfessionsChanged?.Invoke();
    }

    public static void ReloadTree()
    {
        var load = StaticData.LoadResources();
        ResourceLoadError = load.Error;
        Tree = new ResourceTree(load.Value);
        TreeChanged?.Invoke();
    }

    public static void SetTree(ResourceTreeDocument doc)
    {
        Tree = new ResourceTree(doc);
        ResourceLoadError = null;
        TreeChanged?.Invoke();
    }

    public static void SetActiveGalaxy(int? id)
    {
        Settings.ActiveGalaxyId = id;
        SettingsStore.Save(Settings);
        ActiveGalaxyChanged?.Invoke();
    }

    public static void RaiseSpawnDataChanged() => SpawnDataChanged?.Invoke();

    public static string Describe(SyncOutcome o, int maxMessages = 12)
    {
        var lines = new List<string> { o.Summary };
        lines.AddRange(o.Messages.Take(maxMessages));
        if (o.Messages.Count > maxMessages) lines.Add($"(+{o.Messages.Count - maxMessages} more)");
        return string.Join(Environment.NewLine, lines);
    }
}
