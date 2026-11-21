using System.ComponentModel;

namespace ApplicationPackagerUI.Models;

/// <summary>A set of values that a grid cell binds to by the id of its column (<c>Cells[source]</c>); a change of one value tells the binding.</summary>
public sealed class CellBag<T> : INotifyPropertyChanged
{
    private readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every write, with the id of the column; the grid uses it to find out which cell the person edited.</summary>
    public event Action<string>? ValueSet;

    public T? this[string id]
    {
        get => _values.TryGetValue(id, out var value) ? value : default;
        set
        {
            _values[id] = value!;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            ValueSet?.Invoke(id);
        }
    }
}

/// <summary>
/// One application in the Application Settings grid: the check box for the bulk actions, the facts that decide which settings apply to the
/// application, and one cell for each column of the registry (<see cref="Services.ApplicationColumns"/>). A cell holds the value, whether
/// it can be edited, and the tooltip: the reason when the setting does not apply to the application.
/// </summary>
public partial class SupersedenceRow : ObservableObject
{
    /// <summary>The choice that clears the override of the application: it follows the default of Settings.</summary>
    public const string FollowDefault = "Follow the default";

    public required string Uid { get; init; }

    /// <summary>The identifier that keys the setting of the application.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>The identifier of the definition; it keys the saved conditions and command overrides.</summary>
    public string DefinitionId { get; init; } = "";

    public required string Application { get; init; }
    public required string Vendor { get; init; }

    /// <summary>The architecture the definition packages; the fixed value that the package source cell shows when there is no choice.</summary>
    public string Architecture { get; init; } = "";

    /// <summary>True when the definition offers an architecture choice (a second installer or the Architecture split).</summary>
    public bool OffersArchitecture { get; init; }

    /// <summary>True when the installer installs for the system and for the user.</summary>
    public bool OffersInstallScope { get; init; }

    /// <summary>True when the definition supports the Network split.</summary>
    public bool OffersNetworkSplit { get; init; }

    /// <summary>The refusal codes of WSUS for this application, joined with '|'; empty when WSUS can publish it.</summary>
    public string WsusNotSupported { get; init; } = "";

    [ObservableProperty] private bool _isSelected;

    public CellBag<string> Cells { get; } = new();
    public CellBag<bool> Editable { get; } = new();
    public CellBag<string> Tips { get; } = new();

    /// <summary>The list of choices that a cell offers; a cell with a fixed value (no choice for this application) offers that value only.</summary>
    public CellBag<IReadOnlyList<string>> Lists { get; } = new();

    public string ArchitectureText => string.IsNullOrWhiteSpace(Architecture) ? "Fixed" : Architecture + " (fixed)";

    /// <summary>The application name, which a screen reader announces for the grid row.</summary>
    public override string ToString() => Application;
}

/// <summary>What one application offers to the Application Settings grid; see <c>PackagerExecutionService.GridFactsOf</c>.</summary>
public sealed record ApplicationGridFacts(string ApplicationId, string DefinitionId, string Architecture, bool OffersArchitecture, bool OffersInstallScope,
    bool OffersNetworkSplit = false);
