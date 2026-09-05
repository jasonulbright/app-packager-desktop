namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// The facts a compiled NSIS or Inno Setup script yields for packaging, in
/// one shape: literal identity values, the default branch, and one branch
/// per install mode the script accepts.
/// </summary>
public sealed class CompiledScriptFacts
{
    public string Engine { get; init; } = "";
    public bool HeaderAvailable { get; init; }
    /// <summary>Literal DisplayName the script registers; empty when computed at run time or absent.</summary>
    public string DisplayName { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public string Publisher { get; init; } = "";
    /// <summary>AllUsers or CurrentUser: the branch taken without a mode switch.</summary>
    public string DefaultInstallMode { get; init; } = "";
    /// <summary>Modes the script accepts, in the fixed order CurrentUser, AllUsers.</summary>
    public IReadOnlyList<string> InstallModes { get; init; } = [];
    public IReadOnlyDictionary<string, InstallBranch> Branches { get; init; } = new Dictionary<string, InstallBranch>();
    public string Note { get; init; } = "";

    public InstallBranch? DefaultBranch =>
        Branches.TryGetValue(DefaultInstallMode, out var b) ? b : null;

    public static string SourceName(string engine) => engine switch
    {
        "NSIS" => "nsis-compiled-script",
        "InnoSetup" => "inno-setup-header",
        _ => "compiled-script"
    };

    /// <summary>
    /// Reads the installer with the reader for its engine. Null for engines
    /// without a reader or when the header could not be read, so callers
    /// fall back to engine conventions.
    /// </summary>
    public static CompiledScriptFacts? TryRead(string engine, string path)
    {
        try
        {
            switch (engine)
            {
                case "NSIS":
                {
                    var m = NsisScriptReader.Read(path);
                    if (!m.HeaderAvailable) return null;
                    return new CompiledScriptFacts
                    {
                        Engine = engine,
                        HeaderAvailable = true,
                        DisplayName = m.DisplayName,
                        DisplayVersion = m.DisplayVersion,
                        Publisher = m.Publisher,
                        DefaultInstallMode = m.InstallMode,
                        InstallModes = m.InstallModes,
                        Branches = m.ModeVariants,
                        Note = string.Join(" ", new[] { m.Note, m.UninstallRegistryKeyNote }.Where(s => !string.IsNullOrWhiteSpace(s)))
                    };
                }
                case "InnoSetup":
                {
                    var m = InnoSetupReader.Read(path);
                    if (!m.HeaderAvailable) return null;
                    return new CompiledScriptFacts
                    {
                        Engine = engine,
                        HeaderAvailable = true,
                        DisplayName = m.DisplayName,
                        DisplayVersion = m.DisplayVersion,
                        Publisher = m.Publisher,
                        DefaultInstallMode = m.InstallMode,
                        InstallModes = m.InstallModes,
                        Branches = m.ModeVariants,
                        Note = string.Join(" ", new[] { m.Note, m.UninstallRegistryKeyNote }.Where(s => !string.IsNullOrWhiteSpace(s)))
                    };
                }
                default:
                    return null;
            }
        }
        catch
        {
            // A reader failure leaves the drop on the convention path rather than failing the analysis.
            return null;
        }
    }
}
