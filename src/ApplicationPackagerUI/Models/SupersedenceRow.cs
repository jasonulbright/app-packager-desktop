namespace ApplicationPackagerUI.Models;

/// <summary>
/// One application in the supersedence grid of Settings: the check box for the bulk action on the selected applications, and the
/// mode of the application (<see cref="FollowDefault"/> when it has no setting of its own).
/// </summary>
public partial class SupersedenceRow : ObservableObject
{
    /// <summary>The choice that clears the override of the application: it follows the default of Settings.</summary>
    public const string FollowDefault = "Follow the default";

    public required string Uid { get; init; }

    /// <summary>The identifier that keys the setting of the application.</summary>
    public required string ApplicationId { get; init; }

    public required string Application { get; init; }
    public required string Vendor { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _modeChoice = FollowDefault;

    /// <summary>The application name, which a screen reader announces for the grid row.</summary>
    public override string ToString() => Application;
}
