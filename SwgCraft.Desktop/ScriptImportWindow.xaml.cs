using System.Windows;

namespace SwgCraft.Desktop;

public enum ScriptImportChoice { Cancel, Apply, ChangeFolder, PickFile }

/// <summary>Shows what filling a schematic from its SWGEmu script would do, and lets the user apply it, pick another file or folder.</summary>
public partial class ScriptImportWindow : Window
{
    public ScriptImportChoice Choice { get; private set; } = ScriptImportChoice.Cancel;

    public ScriptImportWindow(string header, string report, bool canApply)
    {
        InitializeComponent();
        HeaderText.Text = header;
        ReportBox.Text = report;
        ApplyButton.IsEnabled = canApply;
    }

    void Close(ScriptImportChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ScriptImportChoice.Cancel;
    }

    void Apply_Click(object sender, RoutedEventArgs e) => Close(ScriptImportChoice.Apply);
    void Folder_Click(object sender, RoutedEventArgs e) => Close(ScriptImportChoice.ChangeFolder);
    void File_Click(object sender, RoutedEventArgs e) => Close(ScriptImportChoice.PickFile);
}
