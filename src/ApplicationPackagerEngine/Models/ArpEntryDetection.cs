using System.Text.Json.Serialization;

namespace ApplicationPackagerEngine.Models;

/// <summary>
/// A product's own Add/Remove Programs entry: found by its DisplayName (exact,
/// or a prefix with an optional suffix), an optional Publisher and, with
/// WindowsInstaller, only among entries Windows Installer wrote; installed when
/// its DisplayVersion is at or above Version. The ConfigMgr and Intune
/// detection script and the WSUS applicability rules are both built from it.
/// </summary>
public sealed class ArpEntryDetection
{
    public const string TypeName = "ArpEntry";

    public string Type { get; set; } = TypeName;
    /// <summary>32, 64 or Both.</summary>
    public string View { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayNamePrefix { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayNameSuffix { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Publisher { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WindowsInstaller { get; set; }
    /// <summary>Up to four numeric parts; a definition writes {version} and the stage resolves it.</summary>
    public string Version { get; set; } = "";

    public ArpEntryDetection With(Func<string?, string?> apply) => new()
    {
        Type = Type,
        View = View,
        DisplayName = apply(DisplayName),
        DisplayNamePrefix = apply(DisplayNamePrefix),
        DisplayNameSuffix = apply(DisplayNameSuffix),
        Publisher = apply(Publisher),
        WindowsInstaller = WindowsInstaller,
        Version = apply(Version) ?? ""
    };

    /// <summary>
    /// The entry as an operator reads it: 'Name*Suffix' by Publisher, N-bit
    /// view, and the Windows Installer note.
    /// </summary>
    public string Describe()
    {
        var name = !string.IsNullOrEmpty(DisplayName) ? DisplayName : (DisplayNamePrefix ?? "") + "*" + (DisplayNameSuffix ?? "");
        var tail = WindowsInstaller ? ", Windows Installer entry" : "";
        return !string.IsNullOrEmpty(Publisher)
            ? $"'{name}' by {Publisher}, {View}-bit view{tail}"
            : $"'{name}', {View}-bit view{tail}";
    }
}
