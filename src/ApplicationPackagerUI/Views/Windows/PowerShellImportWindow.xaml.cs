using System.ComponentModel;
using System.Windows;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>One row of the preview. Only a row that differs can be checked, and each starts checked.</summary>
public sealed class PowerShellImportRowView(PowerShellImportRow row) : INotifyPropertyChanged
{
    private bool _selected = row.Differs;

    public string Key => row.Key;
    public string PowerShellValue => row.PowerShellValue;
    public string ApdValue => row.ApdValue;
    public string Note => row.Note;
    public bool Differs => row.Differs;

    public string OutcomeText => row.Outcome switch
    {
        PowerShellImportOutcome.Import => "Differs",
        PowerShellImportOutcome.Same => "Same",
        PowerShellImportOutcome.Skipped => "Skipped",
        _ => "Unknown"
    };

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value || !Differs) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The preview of an import from the PowerShell app: every key of the file with what the import does. Cancel is the default.</summary>
public partial class PowerShellImportWindow
{
    private readonly List<PowerShellImportRowView> _rows;

    public PowerShellImportWindow(PowerShellImportPreview preview)
    {
        InitializeComponent();
        _rows = OrderRows(preview.Rows).Select(r => new PowerShellImportRowView(r)).ToList();
        grid.ItemsSource = _rows;
        txtSource.Text = preview.SourcePath is null ? "" : "File: " + preview.SourcePath;
        txtSummary.Text = preview.Summary;
        btnImport.IsEnabled = preview.Error is null && preview.Differs > 0;
    }

    public PowerShellImportChoice Choice { get; private set; } = new(false, false, new HashSet<string>());

    /// <summary>The rows that differ first, then the same, skipped and unknown rows, each group by key.</summary>
    internal static IEnumerable<PowerShellImportRow> OrderRows(IEnumerable<PowerShellImportRow> rows) =>
        rows.OrderBy(r => r.Outcome).ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase);

    private void OnImport(object sender, RoutedEventArgs e)
    {
        Choice = new PowerShellImportChoice(true, false, _rows.Where(r => r.Differs && r.Selected).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
        DialogResult = true;
    }

    private void OnChooseAnother(object sender, RoutedEventArgs e)
    {
        Choice = new PowerShellImportChoice(false, true, new HashSet<string>());
        DialogResult = false;
    }
}

/// <summary>The WPF prompts of the import: the file picker and the preview window over the active window.</summary>
public sealed class PowerShellImportPrompts : IPowerShellImportPrompts
{
    public string? PickFile(string? currentPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the preferences file of App Packager (PowerShell)",
            Filter = "App Packager preferences (AppPackager.preferences.json)|AppPackager.preferences.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
            InitialDirectory = currentPath is null ? "" : System.IO.Path.GetDirectoryName(currentPath) ?? ""
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public PowerShellImportChoice ShowPreview(PowerShellImportPreview preview)
    {
        var window = new PowerShellImportWindow(preview);
        if (DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow) is { } owner)
            window.Owner = owner;
        window.ShowDialog();
        return window.Choice;
    }
}
