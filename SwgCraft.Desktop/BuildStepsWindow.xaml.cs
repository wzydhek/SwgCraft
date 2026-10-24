using System.IO;
using System.Windows;

namespace SwgCraft.Desktop;

/// <summary>The build plan as a numbered sequence of crafting steps and factory runs (read only, can be copied or saved).</summary>
public partial class BuildStepsWindow : Window
{
    readonly string _fileName;
    readonly Func<Task<(string Text, string Summary)?>>? _refresh;

    /// <param name="refresh">Builds the report again (null hides the Refresh button); a null result keeps what is shown.</param>
    public BuildStepsWindow(string report, string summary, string fileName, Func<Task<(string Text, string Summary)?>>? refresh = null)
    {
        InitializeComponent();
        ReportBox.Text = report;
        SummaryText.Text = summary;
        _fileName = fileName;
        _refresh = refresh;
        if (refresh != null)
        {
            RefreshButton.Visibility = Visibility.Visible;
            KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F5 && RefreshButton.IsEnabled) Refresh_Click(this, new RoutedEventArgs()); };
        }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refresh == null) return;
        RefreshButton.IsEnabled = false;
        var oldSummary = SummaryText.Text;
        SummaryText.Text = "Refreshing...";
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            if (await _refresh() is { } r)
            {
                // keep the reader's place roughly where it was
                var offset = ReportBox.VerticalOffset;
                ReportBox.Text = r.Text;
                SummaryText.Text = $"{r.Summary}   (refreshed {DateTime.Now:HH:mm:ss})";
                ReportBox.ScrollToVerticalOffset(offset);
            }
            else SummaryText.Text = oldSummary;
        }
        catch (Exception ex)
        {
            SummaryText.Text = oldSummary;
            MessageBox.Show(this, "Could not refresh the report:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            RefreshButton.IsEnabled = true;
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ReportBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { MessageBox.Show(this, "The clipboard is busy; try again.", Title); }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = _fileName, DefaultExt = ".txt", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, ReportBox.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not save the report:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
