using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

public partial class MainWindow : Window
{
    ResourceTreeEditorWindow? _resEditor;
    SpawnBrowserWindow? _browser;
    bool _loadingGalaxies;

    public MainWindow() => InitializeComponent();

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var m in Services.StartupMessages) Log(m);
        Log($"Static data folder: {Services.StaticData.Folder}");
        Log($"Database: {Services.Settings.DatabaseFile}");
        if (Services.ResourceLoadError != null) Log("RESOURCE TREE ERROR: " + Services.ResourceLoadError);
        else
        {
            int leaves = Services.Tree.Leaves().Count();
            Log(leaves == 0
                ? "No resource tree yet. Open Static Data > Resource Tree Editor to enter it."
                : $"Resource tree loaded: {leaves} resource types.");
        }
        RefreshGalaxies();
        if (GalaxyCombo.Items.Count == 0)
            Log("No galaxies yet. Add one under Resources > Manage Galaxies (use the numeric ID from the Galaxy Harvester feed URL).");
    }

    // ------------------------------------------------------------ galaxy selection

    void RefreshGalaxies()
    {
        _loadingGalaxies = true;
        try
        {
            var list = Services.Spawns.Galaxies();
            GalaxyCombo.ItemsSource = list;
            GalaxyCombo.DisplayMemberPath = "Label";
            GalaxyCombo.SelectedValuePath = "Id";
            var active = Services.Settings.ActiveGalaxyId;
            if (active == null || list.All(g => g.Id != active))
            {
                active = list.Count > 0 ? list[0].Id : (int?)null;
                Services.SetActiveGalaxy(active);
            }
            GalaxyCombo.SelectedValue = active;
        }
        finally { _loadingGalaxies = false; }
        UpdateStatus();
    }

    void GalaxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGalaxies) return;
        if (GalaxyCombo.SelectedValue is int id) Services.SetActiveGalaxy(id);
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (Services.Settings.ActiveGalaxyId is not int gid) { StatusText.Text = "No active galaxy."; return; }
        var store = Services.Spawns.ForGalaxy(gid);
        var last = store.LastSuccessfulSync();
        StatusText.Text = $"Galaxy {gid}: {store.CurrentCount()} current spawns. " +
                          (last == null ? "Never synced." : $"Last sync {last.Value.ToLocalTime():yyyy-MM-dd HH:mm}.");
    }

    // ------------------------------------------------------------ sync

    async void Sync_Click(object sender, RoutedEventArgs e) => await RunSync(false);
    async void ForceSync_Click(object sender, RoutedEventArgs e) => await RunSync(true);

    async Task RunSync(bool force, bool allowLargeDrop = false)
    {
        if (Services.Settings.ActiveGalaxyId is not int gid)
        {
            MessageBox.Show(this, "Add and select a galaxy first (Resources > Manage Galaxies).", Title);
            return;
        }
        SyncButton.IsEnabled = ForceButton.IsEnabled = false;
        try
        {
            Log($"Syncing galaxy {gid}...");
            var outcome = await Services.Feed.SyncAsync(gid, Services.Tree, force, allowLargeDrop);
            Log(Services.Describe(outcome, 25));
            RefreshGalaxies();
            Services.RaiseSpawnDataChanged();

            if (outcome.State == SyncState.Aborted && !allowLargeDrop &&
                MessageBox.Show(this, outcome.Summary + "\n\nApply the feed anyway?", "Sync aborted",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await RunSync(true, true);
        }
        catch (Exception ex) { Log("Sync error: " + ex.Message); }
        finally { SyncButton.IsEnabled = ForceButton.IsEnabled = true; }
    }

    // ------------------------------------------------------------ menu

    void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Services.StaticData.Folder);
        Process.Start(new ProcessStartInfo(Services.StaticData.Folder) { UseShellExecute = true });
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    void ResourceEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_resEditor != null) { _resEditor.Activate(); return; }
        if (Services.ResourceLoadError != null)
        {
            MessageBox.Show(this, Services.ResourceLoadError + "\n\nFix or move the file, then restart.", "Resource tree",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _resEditor = new ResourceTreeEditorWindow { Owner = this };
        _resEditor.Closed += (_, _) => _resEditor = null;
        _resEditor.Show();
    }

    void SpawnBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_browser != null) { _browser.Activate(); return; }
        _browser = new SpawnBrowserWindow { Owner = this };
        _browser.Closed += (_, _) => _browser = null;
        _browser.Show();
    }

    void Galaxies_Click(object sender, RoutedEventArgs e)
    {
        new GalaxiesWindow { Owner = this }.ShowDialog();
        RefreshGalaxies();
        Services.RaiseSpawnDataChanged();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
        if (OwnedWindows.Count > 0) e.Cancel = true;   // an editor refused to close (unsaved changes)
        base.OnClosing(e);
    }

    void Log(string text)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    InventoryWindow? _inventory;

    void Inventory_Click(object sender, RoutedEventArgs e)
    {
        if (_inventory != null) { _inventory.Activate(); return; }
        _inventory = new InventoryWindow { Owner = this };
        _inventory.Closed += (_, _) => _inventory = null;
        _inventory.Show();
    }

    BuildPlannerWindow? _buildPlanner;

    void BuildPlanner_Click(object sender, RoutedEventArgs e)
    {
        if (_buildPlanner != null) { _buildPlanner.Activate(); return; }
        _buildPlanner = new BuildPlannerWindow { Owner = this };
        _buildPlanner.Closed += (_, _) => _buildPlanner = null;
        _buildPlanner.Show();
    }

    HarvestWindow? _harvest;

    void WthSummary_Click(object sender, RoutedEventArgs e) => WthSummaryWindow.ShowSingle(this);

    void Harvest_Click(object sender, RoutedEventArgs e)
    {
        if (_harvest != null) { _harvest.Activate(); return; }
        _harvest = new HarvestWindow { Owner = this };
        _harvest.Closed += (_, _) => _harvest = null;
        _harvest.Show();
    }

    ItemInventoryWindow? _itemInventory;

    void ItemInventory_Click(object sender, RoutedEventArgs e)
    {
        if (_itemInventory != null) { _itemInventory.Activate(); return; }
        _itemInventory = new ItemInventoryWindow { Owner = this };
        _itemInventory.Closed += (_, _) => _itemInventory = null;
        _itemInventory.Show();
    }

    ProfessionEditorWindow? _profEditor;

    void Professions_Click(object sender, RoutedEventArgs e)
    {
        if (_profEditor != null) { _profEditor.Activate(); return; }
        if (Services.ProfessionLoadError != null)
        {
            MessageBox.Show(this, Services.ProfessionLoadError + "\n\nFix or move the file, then restart.", "Professions",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _profEditor = new ProfessionEditorWindow { Owner = this };
        _profEditor.Closed += (_, _) => _profEditor = null;
        _profEditor.Show();
    }

    LootItemEditorWindow? _lootEditor;
    ClassEditorWindow? _classEditor;
    SchematicEditorWindow? _schematicEditor;

    void LootItems_Click(object sender, RoutedEventArgs e)
    {
        if (_lootEditor != null) { _lootEditor.Activate(); return; }
        var w = new LootItemEditorWindow { Owner = this };
        w.Closed += (_, _) => _lootEditor = null;
        _lootEditor = w;
        w.Show();
    }

    void Classes_Click(object sender, RoutedEventArgs e)
    {
        if (_classEditor != null) { _classEditor.Activate(); return; }
        var w = new ClassEditorWindow { Owner = this };
        w.Closed += (_, _) => _classEditor = null;
        _classEditor = w;
        w.Show();
    }

    void Schematics_Click(object sender, RoutedEventArgs e)
    {
        if (_schematicEditor != null) { _schematicEditor.Activate(); return; }
        var w = new SchematicEditorWindow { Owner = this };
        w.Closed += (_, _) => _schematicEditor = null;
        _schematicEditor = w;
        w.Show();
    }

    StringEditorWindow? _stringEditor;

    void Strings_Click(object sender, RoutedEventArgs e)
    {
        if (_stringEditor != null) { _stringEditor.Activate(); return; }
        var w = new StringEditorWindow { Owner = this };
        w.Closed += (_, _) => _stringEditor = null;
        _stringEditor = w;
        w.Show();
    }

    TabEditorWindow? _tabEditor;

    void Tabs_Click(object sender, RoutedEventArgs e)
    {
        if (_tabEditor != null) { _tabEditor.Activate(); return; }
        var w = new TabEditorWindow { Owner = this };
        w.Closed += (_, _) => _tabEditor = null;
        _tabEditor = w;
        w.Show();
    }

    CategoryEditorWindow? _categoryEditor;

    void Categories_Click(object sender, RoutedEventArgs e)
    {
        if (_categoryEditor != null) { _categoryEditor.Activate(); return; }
        var w = new CategoryEditorWindow { Owner = this };
        w.Closed += (_, _) => _categoryEditor = null;
        _categoryEditor = w;
        w.Show();
    }
}
