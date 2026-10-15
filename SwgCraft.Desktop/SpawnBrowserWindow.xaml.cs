using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwgCraft.Core;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

public sealed record SpawnRow(
    string Name, string TypeName, string TypeId,
    int? ER, int? CR, int? CD, int? DR, int? FL, int? HR, int? MB, int? PE, int? OQ, int? SR, int? UT,
    string Spawned, string Status, string Planets);

public sealed record SyncRow(string Time, string Status, int Added, int Updated, int Reopened, int Replaced,
                             int Despawned, int Unmapped, string Notes);

public partial class SpawnBrowserWindow : Window
{
    public SpawnBrowserWindow()
    {
        InitializeComponent();
        Services.ActiveGalaxyChanged += Refresh;
        Services.SpawnDataChanged += Refresh;
        Services.TreeChanged += Refresh;
        Closed += (_, _) =>
        {
            Services.ActiveGalaxyChanged -= Refresh;
            Services.SpawnDataChanged -= Refresh;
            Services.TreeChanged -= Refresh;
        };
        Refresh();
    }

    void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Refresh(); }
    void Filter_Text_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) Refresh(); }
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void Refresh()
    {
        if (Services.Settings.ActiveGalaxyId is not int gid)
        {
            SpawnGrid.ItemsSource = null;
            SyncGrid.ItemsSource = null;
            StatusText.Text = "No active galaxy selected.";
            return;
        }

        var store = Services.Spawns.ForGalaxy(gid);
        var nameFilter = NameFilter.Text.Trim();
        var typeFilter = TypeFilter.Text.Trim();
        var spawns = store.Spawns(CurrentOnly.IsChecked == true, nameFilter.Length == 0 ? null : nameFilter);

        var rows = spawns.Select(ToRow)
            .Where(r => typeFilter.Length == 0
                        || r.TypeName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)
                        || r.TypeId.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SpawnGrid.ItemsSource = rows;
        StatusText.Text = $"Galaxy {gid}: showing {rows.Count} of {spawns.Count} spawns ({store.CurrentCount()} current).";

        SyncGrid.ItemsSource = store.RecentSyncs(15).Select(s => new SyncRow(
            s.FinishedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), s.Status, s.Added, s.Updated, s.Reopened,
            s.Replaced, s.Despawned, s.Unmapped, (s.Message ?? "").Replace("\n", " | "))).ToList();
    }

    static SpawnRow ToRow(Spawn s)
    {
        var node = Services.Tree.Find(s.ResourceTypeId);
        int? St(Stat stat) => s.Stats.TryGetValue(stat, out var v) ? v : null;
        return new SpawnRow(
            s.Name, node?.Name ?? $"{s.ResourceTypeId} (not in tree)", s.ResourceTypeId,
            St(Stat.ER), St(Stat.CR), St(Stat.CD), St(Stat.DR), St(Stat.FL), St(Stat.HR),
            St(Stat.MB), St(Stat.PE), St(Stat.OQ), St(Stat.SR), St(Stat.UT),
            s.SpawnedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            s.DespawnedUtc == null ? "Current"
            : s.Source == SpawnSources.Manual ? "Past spawn"
            : "Despawned " + s.DespawnedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd"),
            s.Planets);
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Settings.ActiveGalaxyId is not int gid)
        {
            MessageBox.Show(this, "Select a galaxy first.", Title);
            return;
        }
        var dlg = new OpenFileDialog { Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*", Title = "Import Galaxy Harvester XML" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var xml = File.ReadAllText(dlg.FileName);
            var outcome = Services.Feed.ApplyXml(gid, xml, Services.Tree, DateTimeOffset.UtcNow, false);
            MessageBox.Show(this, Services.Describe(outcome), "Import", MessageBoxButton.OK,
                            outcome.State == SyncState.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Services.RaiseSpawnDataChanged();
        }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
