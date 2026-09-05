using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Models;

/// <summary>
/// One definition's install parameters as edited under Settings. The panel writes
/// straight into Values, and Save persists the dictionary per definition Id.
/// </summary>
public sealed class InstallParameterGroup
{
    public required string DefinitionId { get; init; }
    public required string Application { get; init; }
    public required string Vendor { get; init; }
    public required IReadOnlyList<InstallerParameter> Parameters { get; init; }
    public required Dictionary<string, string> Values { get; init; }

    public string Header => $"{Application}";
    public string SubHeader => $"{Vendor} - {Parameters.Count} parameter{(Parameters.Count == 1 ? "" : "s")}";
}
