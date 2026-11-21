namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The condition template document. Each template maps a stable Id
/// (referenced by requirement specs) to a CM global condition. Kind selects
/// the creation cmdlet (BuiltIn is never created, only resolved); RuleType
/// selects the requirement rule cmdlet the spec value binds through.
/// </summary>
public sealed class ConditionTemplateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public ConditionTemplate[] Conditions { get; set; } = [];
}

/// <summary>One global condition definition keyed by <see cref="Id"/>.</summary>
public sealed class ConditionTemplate
{
    public string Id { get; set; } = "";
    public string GlobalConditionName { get; set; } = "";
    /// <summary>BuiltIn, Wql, or Script.</summary>
    public string Kind { get; set; } = "";
    /// <summary>CommonValue, FreeDiskSpace, OperatingSystemLanguage, or Boolean.</summary>
    public string RuleType { get; set; } = "";
    public string? DataType { get; set; }
    public string? Namespace { get; set; }
    public string? Class { get; set; }
    public string? Property { get; set; }
    /// <summary>Friendly value name to the value the condition compares against.</summary>
    public Dictionary<string, string>? Values { get; set; }
    /// <summary>
    /// Narrows a name that matches several site conditions. The stock
    /// Operating System Language exists once per platform: 1 = Windows,
    /// 2 = Mobile.
    /// </summary>
    public int? PlatformType { get; set; }
    public string[]? AdapterPatterns { get; set; }
    public string? AliasPattern { get; set; }
    /// <summary>Explicit script body; overrides the generated VPN script when set.</summary>
    public string[]? ScriptText { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// The ConfigMgr rule operator of a CommonValue or FreeDiskSpace rule: IsEquals (the default), NotEquals,
    /// GreaterThan, GreaterEquals, LessThan or LessEquals.
    /// </summary>
    public string? Operator { get; set; }
    /// <summary>
    /// Turns the whole number of the requirement into the value that the condition compares, for example
    /// 10.0.{0} for the build number in the version of the operating system. A template with it takes a whole number only.
    /// </summary>
    public string? ValueFormat { get; set; }
    /// <summary>
    /// The Intune Win32 app property that carries the requirement instead of a requirement rule:
    /// minimumFreeDiskSpaceInMB, minimumMemoryInMB or minimumSupportedWindowsRelease (a build number).
    /// </summary>
    public string? IntuneProperty { get; set; }
}
