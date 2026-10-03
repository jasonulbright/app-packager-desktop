namespace ApplicationPackagerUI.Models;

/// <summary>
/// One path the operator chooses under Settings: a definition's local
/// installer folder, or a side file such as a license key. Save persists
/// Path under Key.
/// </summary>
public partial class PathSettingRow : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Hint { get; init; } = "";
    public bool IsFolder { get; init; }
    /// <summary>OpenFileDialog filter for a file row.</summary>
    public string FileFilter { get; init; } = "All files (*.*)|*.*";

    [ObservableProperty] private string _path = "";

    [RelayCommand]
    private void Browse()
    {
        if (IsFolder)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Label };
            if (!string.IsNullOrWhiteSpace(Path) && System.IO.Directory.Exists(Path)) dialog.InitialDirectory = Path;
            if (dialog.ShowDialog() == true) Path = dialog.FolderName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = Label, Filter = FileFilter, CheckFileExists = true };
            if (dialog.ShowDialog() == true) Path = dialog.FileName;
        }
    }
}
