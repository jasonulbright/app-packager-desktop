namespace ApplicationPackagerEngine.Models;

/// <summary>What the app says about one failed destination: whether it offers a retry, and the text beside it.</summary>
public sealed record PublishRetryAdvice(bool CanRetry, string Text);

/// <summary>
/// The rules for the retry of a failed destination. The app offers a retry only where it can verify what the destination holds, so a retry
/// never replaces an object and never publishes the same version twice. ConfigMgr is read before the retry, and the retry keeps an existing
/// application. The WSUS publish compares the stage with the update that the server has for the version. The app cannot verify a partly
/// uploaded Intune app, so Intune has no retry.
/// </summary>
public static class PublishRetry
{
    public const string IntuneText = "Verify in Intune before retrying. The app cannot verify what a failed publish left in Intune, so it offers no retry.";

    public const string ConfigMgrLaterStepText =
        "ConfigMgr has the application, and a later step failed. Verify the application in ConfigMgr. The app offers no retry, because a retry would publish the application again.";

    public const string ConfigMgrText =
        "A retry reads ConfigMgr first. It publishes nothing when ConfigMgr has this version or cannot be read, and it does not replace an application.";

    public const string WsusText =
        "A retry compares the stage with the update that WSUS has for this version. It does not change that update, and it refuses a stage that differs.";

    /// <summary>The advice for a cell; null for a cell that did not fail.</summary>
    public static PublishRetryAdvice? For(PublishDestinationResult cell)
    {
        if (cell.Kind != PublishDestinationKind.Failed) return null;
        return cell.Destination switch
        {
            OneClickDestinations.ConfigMgr => cell.Step == "content" ? new(false, ConfigMgrLaterStepText) : new(true, ConfigMgrText),
            OneClickDestinations.Wsus => new(true, WsusText),
            _ => new(false, IntuneText)
        };
    }

    /// <summary>Why a ConfigMgr retry must write nothing, from a read of the site; null when it can go on.</summary>
    public static string? ConfigMgrRefusal(ConfigMgrFact site, string version) => site.Kind switch
    {
        ConfigMgrFactKind.Unreadable => $"ConfigMgr could not be read ({site.Detail}), so the app cannot verify what ConfigMgr has.",
        ConfigMgrFactKind.Found when site.Version.Length == 0 =>
            "ConfigMgr has the application with no version, so the app cannot verify which version it is. Verify the application in ConfigMgr.",
        ConfigMgrFactKind.Found when string.Equals(site.Version, version, StringComparison.OrdinalIgnoreCase) =>
            $"ConfigMgr has version {site.Version}. A retry would publish it again. Verify the application in ConfigMgr.",
        _ => null
    };
}
