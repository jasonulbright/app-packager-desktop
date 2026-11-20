namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// The rules of the supersedence grid in Settings: the choice that a row shows, the edit that a choice makes, and the copy of the settings
/// that the grid edits until Save. Every edit goes through <see cref="SupersedenceSettingsEditor"/>, so nothing here switches supersedence on by itself.
/// </summary>
public static class SupersedenceGrid
{
    /// <summary>The choice that clears the override of an application.</summary>
    public const string FollowDefault = "Follow the default";

    /// <summary>The choices of a row, in the order of the list.</summary>
    public static readonly IReadOnlyList<string> Choices = [FollowDefault, "Off", "Replace", "Replace and uninstall"];

    /// <summary>A copy of the settings that the grid edits; the stored object is not changed until the caller copies the result back.</summary>
    public static SupersedenceSettings Copy(SupersedenceSettings? source)
    {
        source ??= new();
        var copy = new SupersedenceSettings
        {
            ConfigMgrVersionRule = source.ConfigMgrVersionRule,
            IntuneVersionRule = source.IntuneVersionRule,
            Mode = source.Mode,
            InstalledValues = new(source.InstalledValues ?? new(), StringComparer.OrdinalIgnoreCase),
            AcceptedExactBlocks = [.. source.AcceptedExactBlocks ?? []]
        };
        foreach (var (key, entry) in source.TitleOverrides ?? new())
            copy.TitleOverrides[key] = new TitleSupersedenceOverride { Mode = entry.Mode, VersionRule = entry.VersionRule };
        return copy;
    }

    /// <summary>What the row of an application shows: its own mode, or <see cref="FollowDefault"/> when it has none.</summary>
    public static string ChoiceOf(SupersedenceSettings settings, string? applicationId)
    {
        if (SupersedenceResolver.OverrideKey(applicationId) is not { } key || settings.TitleOverrides is null) return FollowDefault;
        var existing = settings.TitleOverrides.Keys.FirstOrDefault(k => string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return FollowDefault;
        return SupersedenceNames.TryParseMode(settings.TitleOverrides[existing].Mode, out var mode) ? SupersedenceNames.Text(mode) : FollowDefault;
    }

    /// <summary>Applies the choice of a row. A word that is not a choice leaves the settings as they are and returns false.</summary>
    public static bool SetChoice(SupersedenceSettings settings, string? applicationId, string? choice)
    {
        if (string.Equals(choice, FollowDefault, StringComparison.Ordinal))
        {
            SupersedenceSettingsEditor.ClearApplication(settings, applicationId);
            return true;
        }
        return SupersedenceNames.TryParseMode(choice, out var mode) && SupersedenceSettingsEditor.SetApplication(settings, applicationId, mode);
    }
}
