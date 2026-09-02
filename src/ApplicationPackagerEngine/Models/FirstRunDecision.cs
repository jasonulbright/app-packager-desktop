namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Decides whether the first-run setup window is due. WPF-free so the decision is exercised
/// headlessly; the window itself only obeys it.
/// </summary>
public static class FirstRunDecision
{
    /// <summary>
    /// A settings file written before the flag existed belongs to a configured user: absence of
    /// the flag in an existing file counts as completed, so only a genuinely missing file is a
    /// first run.
    /// </summary>
    public static bool ResolveCompleted(bool? storedValue, bool settingsFileExisted)
    {
        if (!settingsFileExisted) return false;
        return storedValue ?? true;
    }

    /// <summary>True when the setup window must open after the main window loads.</summary>
    public static bool IsWizardDue(bool? firstRunCompleted) => firstRunCompleted != true;
}
