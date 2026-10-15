using System.Windows;

namespace SwgCraft.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Services.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Startup failed:\n\n" + ex, "SWG Craft Planner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        new MainWindow().Show();
    }
}
