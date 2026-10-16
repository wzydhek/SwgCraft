using System.IO;
using System.Windows;

namespace SwgCraft.Desktop;

/// <summary>The build plan as a numbered sequence of crafting steps and factory runs (read only, can be copied or saved).</summary>
public partial class BuildStepsWindow : Window
{
    readonly string _fileName;

    public BuildStepsWindow(string report, string summary, string fileName)
    {
        InitializeComponent();
        ReportBox.Text = report;
        SummaryText.Text = summary;
        _fileName = fileName;
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
