using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.AddFromInstaller;

/// <summary>The key names of <see cref="DetectionProposal.Values"/>.</summary>
public static class DetectionValueKeys
{
    public const string Hive = "Hive";
    public const string Key = "Key";
    public const string ValueName = "ValueName";
    public const string ExpectedValue = "ExpectedValue";
    public const string Is64Bit = "Is64Bit";
    public const string ProductCode = "ProductCode";
    public const string Path = "Path";
    public const string FileName = "FileName";
    public const string Version = "Version";
}

/// <summary>Turns a reviewed detection proposal into the detection of a definition.</summary>
public static class DetectionProposalMapping
{
    public const string UninstallKeyRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>
    /// True when the proposal gives no usable rule: kind None, a compound rule (edited in the Workbench),
    /// a registry rule with a null or empty key, or a file rule without a folder or a file name.
    /// </summary>
    public static bool IsDefault(DetectionProposal proposal) => ToConfig(proposal) is null;

    /// <summary>The detection of the proposal, or null when <see cref="IsDefault"/> is true. A null or empty registry key is never returned.</summary>
    public static DetectionConfig? ToConfig(DetectionProposal proposal)
    {
        string? Read(string key) =>
            proposal.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var hive = Read(DetectionValueKeys.Hive) ?? "HKLM";
        var is64Bit = !string.Equals(Read(DetectionValueKeys.Is64Bit), "false", StringComparison.OrdinalIgnoreCase);

        switch (proposal.Kind)
        {
            case DetectionKind.RegistryVersion when Read(DetectionValueKeys.Key) is { } key:
                return new DetectionConfig
                {
                    Type = "RegistryKeyValue",
                    RegistryHive = hive,
                    RegistryKeyTemplate = key,
                    ValueName = Read(DetectionValueKeys.ValueName) ?? "DisplayVersion",
                    Operator = "IsEquals",
                    PropertyType = "Version",
                    ExpectedValue = Read(DetectionValueKeys.ExpectedValue),
                    Is64Bit = is64Bit
                };
            case DetectionKind.RegistryExists when Read(DetectionValueKeys.Key) is { } key:
                return new DetectionConfig
                {
                    Type = "RegistryKey",
                    RegistryHive = hive,
                    RegistryKeyTemplate = key,
                    ValueName = null,
                    Operator = null,
                    Is64Bit = is64Bit
                };
            case DetectionKind.MsiProductCode when Read(DetectionValueKeys.ProductCode) is { } code:
                return new DetectionConfig
                {
                    Type = "RegistryKeyValue",
                    RegistryHive = "HKLM",
                    RegistryKeyTemplate = UninstallKeyRoot + code,
                    ValueName = "DisplayVersion",
                    Operator = "IsEquals",
                    PropertyType = "Version",
                    ExpectedValue = Read(DetectionValueKeys.Version),
                    Is64Bit = is64Bit
                };
            case DetectionKind.FileVersion when Read(DetectionValueKeys.Path) is { } path && Read(DetectionValueKeys.FileName) is { } file:
                return new DetectionConfig
                {
                    Type = "File",
                    RegistryHive = null,
                    ValueName = null,
                    FilePath = path,
                    FileName = file,
                    Operator = "GreaterEquals",
                    PropertyType = "Version",
                    ExpectedValue = Read(DetectionValueKeys.Version),
                    Is64Bit = is64Bit
                };
            case DetectionKind.FileExists when Read(DetectionValueKeys.Path) is { } path && Read(DetectionValueKeys.FileName) is { } file:
                return new DetectionConfig
                {
                    Type = "File",
                    RegistryHive = null,
                    ValueName = null,
                    Operator = null,
                    FilePath = path,
                    FileName = file,
                    PropertyType = "Existence",
                    Is64Bit = is64Bit
                };
            default:
                return null;
        }
    }
}
