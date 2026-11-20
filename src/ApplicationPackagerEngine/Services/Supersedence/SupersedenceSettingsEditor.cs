namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// The edits of the per-application supersedence setting: one default (Off when nothing is stored), one optional override for each
/// application, and the bulk choices all applications, selected applications and no applications. Nothing here switches supersedence
/// on by itself; every call is the choice of the person who pressed the button.
/// </summary>
public static class SupersedenceSettingsEditor
{
    /// <summary>The note that the version in the title is locked while supersedence is on, because n and n-1 must be separate applications.</summary>
    public const string TitleLockNote = "Supersedence needs the version in the title, so the title of this application includes the version.";

    /// <summary>The mode that one application has: its override, else the default.</summary>
    public static SupersedenceMode EffectiveMode(SupersedenceSettings settings, string? applicationId)
    {
        try { return SupersedenceResolver.Resolve(settings, applicationId, null).Mode.Value; }
        catch (InvalidOperationException) { return SupersedenceMode.Off; }
    }

    /// <summary>
    /// The refusal text when "Replace and uninstall" would be set for the application while its ConfigMgr version rule is Minimum (the
    /// publish refuses that combination, see <see cref="SupersedenceResolver.BlockedCombination"/>); null when the combination is allowed
    /// or the mode is another one.
    /// </summary>
    public static string? UninstallBlockedFor(SupersedenceSettings settings, string? applicationId, SupersedenceMode mode)
    {
        if (mode != SupersedenceMode.ReplaceAndUninstall) return null;
        try
        {
            var rule = SupersedenceResolver.Resolve(settings, applicationId, null).ConfigMgrRule.Value;
            return SupersedenceResolver.BlockedCombination(rule, mode);
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The same for the default of all applications: it is blocked when the default ConfigMgr version rule is Minimum.</summary>
    public static string? UninstallBlockedForAll(SupersedenceSettings settings, SupersedenceMode mode)
    {
        if (mode != SupersedenceMode.ReplaceAndUninstall) return null;
        var rule = SupersedenceNames.TryParseRule(settings.ConfigMgrVersionRule, out var parsed) ? parsed : VersionRule.Minimum;
        return SupersedenceResolver.BlockedCombination(rule, mode);
    }

    /// <summary>True when supersedence is on for the application, so the version in its title is forced and locked.</summary>
    public static bool LocksVersionInTitle(SupersedenceSettings settings, string? applicationId, SupersedenceCommandLine? commandLine = null)
    {
        try { return SupersedenceResolver.Resolve(settings, applicationId, commandLine).Mode.Value != SupersedenceMode.Off; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Sets the mode of one application. A mode equal to the default clears the override, so the file holds only what differs.
    /// Returns false for an application without an identifier (it has no key).
    /// </summary>
    public static bool SetApplication(SupersedenceSettings settings, string? applicationId, SupersedenceMode mode)
    {
        if (SupersedenceResolver.OverrideKey(applicationId) is not { } key) return false;
        settings.TitleOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        var existing = settings.TitleOverrides.Keys.FirstOrDefault(k => string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase));
        TitleSupersedenceOverride? entry = existing is null ? null : settings.TitleOverrides[existing];
        var defaultMode = SupersedenceNames.TryParseMode(settings.Mode, out var parsed) ? parsed : SupersedenceMode.Off;
        if (mode == defaultMode)
        {
            if (entry is null) return true;
            entry.Mode = null;
            if (string.IsNullOrWhiteSpace(entry.VersionRule)) settings.TitleOverrides.Remove(existing!);
            return true;
        }
        if (entry is null)
        {
            entry = new TitleSupersedenceOverride();
            settings.TitleOverrides[key] = entry;
        }
        entry.Mode = SupersedenceNames.Text(mode);
        return true;
    }

    /// <summary>Clears the override of one application, so it follows the default again.</summary>
    public static void ClearApplication(SupersedenceSettings settings, string? applicationId)
    {
        if (SupersedenceResolver.OverrideKey(applicationId) is not { } key || settings.TitleOverrides is null) return;
        var existing = settings.TitleOverrides.Keys.FirstOrDefault(k => string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        var entry = settings.TitleOverrides[existing];
        entry.Mode = null;
        if (string.IsNullOrWhiteSpace(entry.VersionRule)) settings.TitleOverrides.Remove(existing);
    }

    /// <summary>Selected applications: each gets the mode as its own override.</summary>
    public static int SetSelected(SupersedenceSettings settings, IEnumerable<string?> applicationIds, SupersedenceMode mode)
    {
        var changed = 0;
        foreach (var id in applicationIds)
            if (SetApplication(settings, id, mode)) changed++;
        return changed;
    }

    /// <summary>
    /// All applications (or none, with Off): the default takes the mode and the single overrides of the mode are cleared, so every
    /// application follows the default. The version rule of an override is not touched.
    /// </summary>
    public static void SetAll(SupersedenceSettings settings, SupersedenceMode mode)
    {
        settings.Mode = SupersedenceNames.Text(mode);
        if (settings.TitleOverrides is null) return;
        foreach (var key in settings.TitleOverrides.Keys.ToList())
        {
            var entry = settings.TitleOverrides[key];
            entry.Mode = null;
            if (string.IsNullOrWhiteSpace(entry.VersionRule)) settings.TitleOverrides.Remove(key);
        }
    }
}
