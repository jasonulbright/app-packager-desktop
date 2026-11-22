using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

public enum SiteSwitchOutcome
{
    /// <summary>The active site changed.</summary>
    Switched,
    /// <summary>The named site is already active. Nothing changed.</summary>
    NoChange,
    /// <summary>A run is active. Nothing changed.</summary>
    Refused,
    /// <summary>The user cancelled the question about a reused site code. Nothing changed.</summary>
    Cancelled,
    /// <summary>The site code or the site is not valid. Nothing changed.</summary>
    Invalid,
}

public sealed record SiteSwitchResult(SiteSwitchOutcome Outcome, string Message, string? FolderName)
{
    public bool Succeeded => Outcome is SiteSwitchOutcome.Switched or SiteSwitchOutcome.NoChange;
}

/// <summary>
/// The sites of the user and the switch between them (Q-112d, B4). A switch changes only which site folder every store reads. It writes
/// nothing into the site that was active and nothing into the site that becomes active: the next save of settings writes the new site.
/// A switch is refused while a run is active.
/// </summary>
public sealed class SiteManager
{
    public const string RunActiveMessage = "The site cannot change while {0}. Wait until it ends or cancel it, then switch the site.";

    private readonly Func<string?> _activeRun;
    private readonly string? _dataRoot;

    public SiteManager(Func<string?>? activeRun = null, string? dataRoot = null)
    {
        _activeRun = activeRun ?? SiteRunGuard.ActiveRun;
        _dataRoot = dataRoot;
    }

    /// <summary>Raised after the active site changed. Pages reload their site data when it is raised.</summary>
    public event Action? Changed;

    public string? ActiveFolderName => SiteContext.ActiveFolderName;

    public IReadOnlyList<SiteInfo> Sites() => SiteCatalog.List(_dataRoot);

    /// <summary>The refusal text when a run is active; null when the site may change.</summary>
    public string? Refusal() => _activeRun() is { } run ? string.Format(RunActiveMessage, run) : null;

    /// <summary>Makes a known site the active one.</summary>
    public SiteSwitchResult Switch(string folderName)
    {
        if (Refusal() is { } refusal) return new SiteSwitchResult(SiteSwitchOutcome.Refused, refusal, null);
        if (!SiteDataPaths.TryParseFolderName(folderName, out var parsed) || parsed is null || SiteCatalog.Get(parsed.FolderName, _dataRoot) is null)
            return new SiteSwitchResult(SiteSwitchOutcome.Invalid, $"The site '{folderName}' is not known.", null);
        if (string.Equals(SiteContext.ActiveFolderName, parsed.FolderName, StringComparison.Ordinal))
            return new SiteSwitchResult(SiteSwitchOutcome.NoChange, $"Site {parsed.FolderName} is already active.", parsed.FolderName);
        Activate(parsed.FolderName);
        return new SiteSwitchResult(SiteSwitchOutcome.Switched, $"Site {parsed.FolderName} is active. Its settings and data are loaded; nothing of the previous site is shown.", parsed.FolderName);
    }

    /// <summary>
    /// Adds a site and makes it active. A code that a site folder already uses with another provider asks <paramref name="ask"/>:
    /// the same site with another provider (the folder is used and lists the provider), or a different environment (a new folder AB1~2).
    /// </summary>
    public SiteSwitchResult AddSite(string siteCode, string? provider, string? label, Func<SiteInfo, SiteReuseAnswer> ask)
    {
        if (Refusal() is { } refusal) return new SiteSwitchResult(SiteSwitchOutcome.Refused, refusal, null);
        var code = (siteCode ?? "").Trim();
        if (!SiteDataPaths.IsValidSiteCode(code))
            return new SiteSwitchResult(SiteSwitchOutcome.Invalid, InvalidCodeMessage(code), null);
        var match = SiteCatalog.Match(code, provider, _dataRoot);
        SiteInfo target;
        switch (match.Kind)
        {
            case SiteMatchKind.New:
                target = SiteCatalog.Create(code, provider, label, _dataRoot);
                break;
            case SiteMatchKind.Existing:
                target = match.Existing!;
                break;
            default:
                switch (ask(match.Existing!))
                {
                    case SiteReuseAnswer.SameSiteOtherProvider:
                        target = SiteCatalog.AddProvider(match.Existing!.FolderName, provider ?? "", _dataRoot);
                        break;
                    case SiteReuseAnswer.DifferentEnvironment:
                        target = SiteCatalog.Create(code, provider, label, _dataRoot);
                        break;
                    default:
                        return new SiteSwitchResult(SiteSwitchOutcome.Cancelled, "The site was not added.", null);
                }
                break;
        }
        return Switch(target.FolderName);
    }

    public static string InvalidCodeMessage(string code) =>
        code.Length == 0 ? "Enter the site code: three letters or digits."
        : SiteDataPaths.ReservedSiteCodes.Contains(code.ToUpperInvariant()) ? $"'{code}' is reserved by Windows or ConfigMgr and cannot be a site code."
        : $"'{code}' is not a site code: use exactly three letters or digits.";

    private void Activate(string folderName)
    {
        SiteContext.Activate(folderName, _dataRoot);
        try
        {
            SiteCatalog.WriteActiveSite(folderName, _dataRoot);
            SiteCatalog.Touch(folderName, _dataRoot);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // The switch holds for this process; the next start picks the site again from settings.
        }
        Changed?.Invoke();
    }
}
