namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Seam over the Windows Installer COM API so analysis logic can be exercised
/// without a real .msi on disk.
/// </summary>
public interface IMsiInspector
{
    /// <summary>Reads the MSI Property table rows the packager depends on.</summary>
    MsiProperties ReadProperties(string msiPath);

    /// <summary>
    /// Reads the architecture half of the SummaryInformation Template field
    /// ("Intel", "x64", "Arm64"). Returns an empty string when unavailable —
    /// the Property table carries no x86/x64 signal, so this is the only source.
    /// </summary>
    string ReadTemplateArchitecture(string msiPath);
}
