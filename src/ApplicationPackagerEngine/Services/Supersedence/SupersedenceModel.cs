using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>Whether the version clause of a detection says "this version" or "this version or newer".</summary>
public enum VersionRule { Minimum, Exact }

/// <summary>How the new ConfigMgr application relates to an older one.</summary>
public enum SupersedenceMode { Off, Replace, ReplaceAndUninstall }

/// <summary>The words of the two settings, as the settings file and the command line spell them.</summary>
public static class SupersedenceNames
{
    public static bool TryParseRule(string? text, out VersionRule rule)
    {
        rule = VersionRule.Minimum;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "minimum": rule = VersionRule.Minimum; return true;
            case "exact": rule = VersionRule.Exact; return true;
            default: return false;
        }
    }

    public static bool TryParseMode(string? text, out SupersedenceMode mode)
    {
        mode = SupersedenceMode.Off;
        switch ((text ?? "").Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-"))
        {
            case "off": mode = SupersedenceMode.Off; return true;
            case "replace": mode = SupersedenceMode.Replace; return true;
            case "replace-uninstall": case "replaceanduninstall": case "replace-and-uninstall":
                mode = SupersedenceMode.ReplaceAndUninstall; return true;
            default: return false;
        }
    }

    public static string Text(VersionRule rule) => rule == VersionRule.Exact ? "Exact" : "Minimum";

    public static string Text(SupersedenceMode mode) => mode switch
    {
        SupersedenceMode.Replace => "Replace",
        SupersedenceMode.ReplaceAndUninstall => "Replace and uninstall",
        _ => "Off"
    };
}

/// <summary>The override of one title: either value can be left unset.</summary>
public sealed class TitleSupersedenceOverride
{
    public string? VersionRule { get; set; }
    public string? Mode { get; set; }
}

/// <summary>The stored settings of the version rule and the supersedence mode, as the publish reads them.</summary>
public sealed class SupersedenceSettings
{
    /// <summary>Site default for ConfigMgr. Shipped: Minimum.</summary>
    public string ConfigMgrVersionRule { get; set; } = "Minimum";
    /// <summary>Site default for Intune. Shipped: Minimum.</summary>
    public string IntuneVersionRule { get; set; } = "Minimum";
    /// <summary>Site default of the supersedence mode. Shipped: Off.</summary>
    public string Mode { get; set; } = "Off";
    /// <summary>Per-title overrides. The key is the definition identifier of a catalog title (see <see cref="SupersedenceResolver.OverrideKey"/>).</summary>
    public Dictionary<string, TitleSupersedenceOverride> TitleOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Installed values that a test install read, keyed by <see cref="ExactGuard.RecordKey"/>.</summary>
    public Dictionary<string, string> InstalledValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Blocks of the Exact guard that the administrator accepted, per title and per version.</summary>
    public List<string> AcceptedExactBlocks { get; set; } = [];
}

/// <summary>The settings that the command line gives; they outrank the other levels.</summary>
public sealed record SupersedenceCommandLine(string? VersionRule, string? Mode);
