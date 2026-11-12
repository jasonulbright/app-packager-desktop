namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>Where a resolved value came from.</summary>
public enum SettingSource { SiteDefault, TitleOverride, CommandLine }

public sealed record ResolvedSetting<T>(T Value, SettingSource Source);

/// <summary>The two settings of one publish after resolution, and the blocked combination if there is one.</summary>
public sealed record ResolvedSupersedence(
    ResolvedSetting<VersionRule> ConfigMgrRule,
    ResolvedSetting<VersionRule> IntuneRule,
    ResolvedSetting<SupersedenceMode> Mode)
{
    /// <summary>The refusal text, or null when the combination is allowed.</summary>
    public string? Blocked => SupersedenceResolver.BlockedCombination(ConfigMgrRule.Value, Mode.Value);
}

/// <summary>
/// Resolves each setting by itself in the order command line, per-title override, site default, and checks the blocked combination
/// after both are resolved.
/// </summary>
public static class SupersedenceResolver
{
    public const string BlockedText =
        "Replace and uninstall needs the version rule Exact. With Minimum, the detection of the old application still finds the new " +
        "version after the uninstall, and the uninstall is reported as failed. Set the version rule to Exact, or set the supersedence " +
        "mode to Replace or Off.";

    /// <summary>The key of a per-title override: the definition identifier of a catalog title; null for a title that has none.</summary>
    public static string? OverrideKey(string? applicationId) =>
        string.IsNullOrWhiteSpace(applicationId) ? null : applicationId.Trim();

    public static string? BlockedCombination(VersionRule configMgrRule, SupersedenceMode mode) =>
        mode == SupersedenceMode.ReplaceAndUninstall && configMgrRule == VersionRule.Minimum ? BlockedText : null;

    /// <exception cref="InvalidOperationException">A value in the settings or on the command line is not one of the allowed words.</exception>
    public static ResolvedSupersedence Resolve(SupersedenceSettings settings, string? applicationId, SupersedenceCommandLine? commandLine)
    {
        TitleSupersedenceOverride? title = null;
        if (OverrideKey(applicationId) is { } key && settings.TitleOverrides is { } overrides)
            title = overrides.FirstOrDefault(o => string.Equals(o.Key.Trim(), key, StringComparison.OrdinalIgnoreCase)).Value;

        ResolvedSetting<VersionRule> Rule(string? cli, string? over, string site, string name)
        {
            if (!string.IsNullOrWhiteSpace(cli)) return new(ParseRule(cli, "the command line option --version-rule"), SettingSource.CommandLine);
            if (!string.IsNullOrWhiteSpace(over)) return new(ParseRule(over, $"the override of this title ({name})"), SettingSource.TitleOverride);
            return new(ParseRule(site, $"the site default ({name})"), SettingSource.SiteDefault);
        }

        // The per-title override sets one version rule for the title; it applies to both targets.
        var configMgrRule = Rule(commandLine?.VersionRule, title?.VersionRule, settings.ConfigMgrVersionRule, "ConfigMgr version rule");
        var intuneRule = Rule(commandLine?.VersionRule, title?.VersionRule, settings.IntuneVersionRule, "Intune version rule");

        ResolvedSetting<SupersedenceMode> mode;
        if (!string.IsNullOrWhiteSpace(commandLine?.Mode))
            mode = new(ParseMode(commandLine!.Mode, "the command line option --supersedence"), SettingSource.CommandLine);
        else if (!string.IsNullOrWhiteSpace(title?.Mode))
            mode = new(ParseMode(title!.Mode, "the override of this title"), SettingSource.TitleOverride);
        else
            mode = new(ParseMode(settings.Mode, "the site default"), SettingSource.SiteDefault);
        return new ResolvedSupersedence(configMgrRule, intuneRule, mode);
    }

    private static VersionRule ParseRule(string text, string where) =>
        SupersedenceNames.TryParseRule(text, out var rule) ? rule
            : throw new InvalidOperationException($"The version rule '{text}' in {where} is not Minimum or Exact.");

    private static SupersedenceMode ParseMode(string text, string where) =>
        SupersedenceNames.TryParseMode(text, out var mode) ? mode
            : throw new InvalidOperationException($"The supersedence mode '{text}' in {where} is not Off, Replace or Replace and uninstall.");
}
