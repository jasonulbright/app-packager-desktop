namespace ApplicationPackagerUI.Models;

/// <summary>
/// One application in the Application Settings grid: the check box for the bulk actions on the checked applications, and the choice of
/// each column. A choice that the application does not offer (the package source of an application with one architecture, the install
/// scope of an installer with one scope) has its column disabled; the cell then shows the fixed value.
/// </summary>
public partial class SupersedenceRow : ObservableObject
{
    /// <summary>The choice that clears the override of the application: it follows the default of Settings.</summary>
    public const string FollowDefault = "Follow the default";

    public required string Uid { get; init; }

    /// <summary>The identifier that keys the setting of the application.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>The identifier of the definition; it keys the saved conditions (package source, install scope, version in title).</summary>
    public string DefinitionId { get; init; } = "";

    public required string Application { get; init; }
    public required string Vendor { get; init; }

    /// <summary>The architecture the definition packages; the fixed value that the package source cell shows when there is no choice.</summary>
    public string Architecture { get; init; } = "";

    /// <summary>True when the definition offers an architecture choice (a second payload or the Architecture split).</summary>
    public bool OffersArchitecture { get; init; }

    /// <summary>True when the installer installs for the system and for the user.</summary>
    public bool OffersInstallScope { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _modeChoice = FollowDefault;
    [ObservableProperty] private string _architectureChoice = "";
    [ObservableProperty] private string _scopeChoice = "";
    [ObservableProperty] private string _titleChoice = FollowDefault;
    [ObservableProperty] private string _ruleChoice = FollowDefault;

    /// <summary>True while supersedence is on for the application: the version in the title is forced and locked.</summary>
    [ObservableProperty] private bool _titleLocked;

    /// <summary>The cell of the package source column: a choice where one is offered, else the fixed architecture.</summary>
    public IReadOnlyList<string> ArchitectureCells => OffersArchitecture ? Services.ApplicationGridSettings.ArchitectureChoices : [ArchitectureText];

    /// <summary>The cell of the install scope column.</summary>
    public IReadOnlyList<string> ScopeCells => OffersInstallScope ? Services.ApplicationGridSettings.ScopeChoices : ["Per installer"];

    public string ArchitectureText => string.IsNullOrWhiteSpace(Architecture) ? "Fixed" : Architecture + " (fixed)";

    /// <summary>The application name, which a screen reader announces for the grid row.</summary>
    public override string ToString() => Application;
}

/// <summary>What one application offers to the Application Settings grid; see <c>PackagerExecutionService.GridFactsOf</c>.</summary>
public sealed record ApplicationGridFacts(string ApplicationId, string DefinitionId, string Architecture, bool OffersArchitecture, bool OffersInstallScope);
