namespace ApplicationPackagerUI.Models;

public partial class PackagerInfo : ObservableObject
{
    // Mutable — bound to DataGrid, updated during operations
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _currentVersion = "";
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private string _status = "Ready";

    // Immutable metadata — set once during discovery
    public string Vendor { get; init; } = "";
    public string Application { get; init; } = "";
    public string CMName { get; init; } = "";
    public string Script { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string VendorUrl { get; init; } = "";
    public string Description { get; init; } = "";

    public bool IsRunnable => !Script.EndsWith(".notps1", StringComparison.OrdinalIgnoreCase);
}
