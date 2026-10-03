namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Operator settings a definition stage reads besides its install parameters.
/// A definition that declares none of the matching fields ignores them.
/// </summary>
public sealed class StageInputs
{
    /// <summary>The folder chosen for this definition's local installer source.</summary>
    public string? LocalSourceFolder { get; init; }

    /// <summary>Chosen side-file paths by SideFileConfig.Key.</summary>
    public IReadOnlyDictionary<string, string>? SideFiles { get; init; }

    /// <summary>Citrix Workspace switch settings for a definition that names a switch stream.</summary>
    public CitrixWorkspaceSwitches? CitrixSwitches { get; init; }
}
