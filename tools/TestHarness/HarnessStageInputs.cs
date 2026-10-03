using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;

/// <summary>
/// The operator settings a stage reads, loaded from the --stage-inputs file: the values
/// the application keeps in its settings and passes to staging.
/// </summary>
public sealed class HarnessStageInputs
{
    public Dictionary<string, string>? LocalSourceFolders { get; set; }
    public Dictionary<string, string>? SideFiles { get; set; }
    public Dictionary<string, Dictionary<string, string>>? InstallerSettings { get; set; }
    public SigningPolicy? SigningPolicy { get; set; }

    public static HarnessStageInputs Load(string path) =>
        JsonSerializer.Deserialize<HarnessStageInputs>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException($"Stage inputs file '{path}' is empty.");

    public Dictionary<string, string>? SettingsFor(AppDefinition def) =>
        InstallerSettings is not null && InstallerSettings.TryGetValue(def.Id, out var values) ? values : null;

    public StageInputs InputsFor(AppDefinition def) => new()
    {
        LocalSourceFolder = LocalSourceFolders is not null && LocalSourceFolders.TryGetValue(def.Id, out var folder) ? folder : null,
        SideFiles = SideFiles,
        SigningPolicy = SigningPolicy is null ? null : SigningPolicy.FromSettings(SigningPolicy)
    };
}
