using System.Windows;
using System.Windows.Controls;
using SwgCraft.Data;

namespace SwgCraft.Desktop;

public partial class GalaxiesWindow : Window
{
    public GalaxiesWindow()
    {
        InitializeComponent();
        Reload();
    }

    void Reload() => GalaxyGrid.ItemsSource = Services.Spawns.Galaxies();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GalaxyGrid.SelectedItem is Galaxy g) { IdBox.Text = g.Id.ToString(); NameBox.Text = g.Name; }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IdBox.Text.Trim(), out var id) || id <= 0)
        {
            MessageBox.Show(this, "Enter the numeric galaxy ID.", Title);
            return;
        }
        var name = NameBox.Text.Trim();
        Services.Spawns.UpsertGalaxy(id, name.Length == 0 ? $"Galaxy {id}" : name);
        if (Services.Settings.ActiveGalaxyId == null) Services.SetActiveGalaxy(id);
        Reload();
    }
}
