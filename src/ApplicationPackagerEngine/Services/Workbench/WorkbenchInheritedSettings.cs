using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Everything below the profile for one application: global defaults plus the
/// packager's own metadata. A value only a completed Stage can resolve reads as
/// <see cref="NeedsStagingMarker"/> and is never replaced with an invented default.
/// </summary>
public sealed class WorkbenchInheritedSettings
{
    public const string NeedsStagingMarker = "(needs staging)";
    public const string TitlePackagerDefault = "Packager default";
    public const string TitleIncludeVersion = "Include version";

    public string Source { get; init; } = "Packager default";
    public string DisplayName { get; init; } = NeedsStagingMarker;
    public string Publisher { get; init; } = NeedsStagingMarker;
    public string Description { get; init; } = "";
    public string TitleMode { get; init; } = TitlePackagerDefault;
    public string InstallCommand { get; init; } = NeedsStagingMarker;
    public string UninstallCommand { get; init; } = NeedsStagingMarker;
    public string DetectionSummary { get; init; } = NeedsStagingMarker;
    public string RebootPolicy { get; init; } = "Packager default";
    public string WorkingDirectory { get; init; } = "";
    public int EstimatedMinutes { get; init; } = 15;
    public int MaximumMinutes { get; init; } = 30;
    public string ExecutionContext { get; init; } = "System";
    public string LogonRequirement { get; init; } = "Whether or not a user is logged on";
    public string UserInteraction { get; init; } = "Hidden";
    public string ScriptHost { get; init; } = "x64";

    /// <summary>
    /// Resolves the inherited values through <see cref="EffectiveSettingsResolver"/> with
    /// only the global level supplied: application metadata names the display name and
    /// publisher, the global timing preferences supply both durations, and every
    /// stage-only field reports <see cref="NeedsStagingMarker"/>.
    /// </summary>
    public static WorkbenchInheritedSettings Build(
        string? displayName, string? publisher, string? description,
        int globalEstimatedMinutes, int globalMaximumMinutes, bool includeVersionInTitle = false)
    {
        var globals = WorkbenchJson.NewObject();
        globals["EstimatedRuntimeMins"] = globalEstimatedMinutes;
        globals["MaximumRuntimeMins"] = globalMaximumMinutes;
        globals["Description"] = description ?? "";
        if (includeVersionInTitle) globals["TitleMode"] = "IncludeVersion";
        var resolved = EffectiveSettingsResolver.Resolve(globals);

        string Pick(string field, string fallback)
        {
            if (!resolved.TryGet(field, out var entry)) return fallback;
            if (entry.Source == SettingSource.NeedsStaging) return NeedsStagingMarker;
            var text = WorkbenchJson.AsText(entry.Value);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        int PickMinutes(string field, int fallback) =>
            int.TryParse(Pick(field, ""), out var minutes) ? minutes : fallback;

        return new WorkbenchInheritedSettings
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Pick("DisplayName", NeedsStagingMarker) : displayName,
            Publisher = string.IsNullOrWhiteSpace(publisher) ? Pick("Publisher", NeedsStagingMarker) : publisher,
            Description = Pick("Description", ""),
            TitleMode = includeVersionInTitle ? TitleIncludeVersion : TitlePackagerDefault,
            InstallCommand = Pick("InstallCommand", NeedsStagingMarker),
            UninstallCommand = Pick("UninstallCommand", NeedsStagingMarker),
            DetectionSummary = Pick("Detection", NeedsStagingMarker),
            EstimatedMinutes = PickMinutes("EstimatedMinutes", globalEstimatedMinutes),
            MaximumMinutes = PickMinutes("MaximumMinutes", globalMaximumMinutes),
            ExecutionContext = Pick("Context", "System"),
            LogonRequirement = Pick("LogonRequirement", "Whether or not a user is logged on"),
            UserInteraction = Pick("UserInteraction", "Hidden"),
            ScriptHost = Pick("ScriptHost", "x64")
        };
    }

    /// <summary>The inherited value a profile path falls back to, as display text.</summary>
    public string ForPath(string path) => path switch
    {
        "Application.DisplayName" => DisplayName,
        "Application.Publisher" => Publisher,
        "Application.Description" => Description,
        "Application.TitleMode" => TitleMode,
        "Install.Command" => InstallCommand,
        "Uninstall.Command" => UninstallCommand,
        "Install.Mode" or "Uninstall.Mode" => "Generated",
        "Detection.Mode" => "Inherit",
        "InstallMode" => "Packager default",
        "Timing.EstimatedMinutes" => EstimatedMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "Timing.MaximumMinutes" => MaximumMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "Execution.Context" => ExecutionContext,
        "Execution.LogonRequirement" => LogonRequirement,
        "Execution.UserInteraction" => UserInteraction,
        "Execution.ScriptHost" => ScriptHost,
        _ => "Inherit"
    };
}
