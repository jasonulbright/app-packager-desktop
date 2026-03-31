namespace ApplicationPackagerUI.Models;

public partial class AppVisibilityItem : ObservableObject
{
    public string Application { get; init; } = "";
    public string Script { get; init; } = "";

    [ObservableProperty] private bool _isVisible = true;
}

public partial class VendorGroup : ObservableObject
{
    public string Vendor { get; init; } = "";
    public ObservableCollection<AppVisibilityItem> Apps { get; init; } = [];

    [ObservableProperty] private bool _isExpanded = true;

    [ObservableProperty] private bool? _isAllVisible = true;

    /// <summary>
    /// Recalculate the tri-state header checkbox from child states.
    /// </summary>
    public void UpdateGroupState()
    {
        var allVisible = Apps.All(a => a.IsVisible);
        var noneVisible = Apps.All(a => !a.IsVisible);
        IsAllVisible = allVisible ? true : noneVisible ? false : null;
    }
}
