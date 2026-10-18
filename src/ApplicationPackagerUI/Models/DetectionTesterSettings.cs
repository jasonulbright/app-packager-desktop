namespace ApplicationPackagerUI.Models;

/// <summary>The last clause type, hive and registry view the Detection Tester page ran.</summary>
public sealed class DetectionTesterSettings
{
    public string ClauseType { get; set; } = "RegistryKeyValue";

    /// <summary>HKLM or HKCU.</summary>
    public string Hive { get; set; } = "HKLM";

    /// <summary>64-bit or 32-bit.</summary>
    public string View { get; set; } = "64-bit";
}
