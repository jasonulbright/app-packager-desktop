namespace ApplicationPackagerEngine.Tests.GuideImages;

/// <summary>
/// The capture steps of the guide images: a guide image id and the page whose default view it shows. A check run that shows a page is a
/// capture run (decision D-21): the capture test renders each page at the work area of a maximized window on a 1080p display (1920 x 1032 at
/// 100 percent, measured on CLIENT01) in both themes and saves the image under its id. An image that no step produces is listed in GuideImages.pending.txt.
/// </summary>
public static class GuideCaptureSteps
{
    /// <summary>The work area of a maximized window on a 1080p display with the Windows 11 taskbar (D-20).</summary>
    public const int WorkAreaWidth = 1920, WorkAreaHeight = 1032;

    /// <summary>The width that the main window gives to the open navigation pane and its border; the page gets the rest.</summary>
    public const double NavigationPaneWidth = 230;

    /// <summary>The height of the window frame that the main window keeps around the page.</summary>
    public const double FrameHeight = 48;

    /// <summary>Guide image id to page name (the names of <c>PageHeaderRenderTests.PageNames</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> PageOfImage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["core-add-app-step1"] = "Add from web",
        ["core-add-file-pick"] = "Add from file",
        ["core-settings-top"] = "Settings",
    };

    /// <summary>
    /// Guide image id to the page that the lab capture run saves (tools\guide-capture\Capture-ApdPages.ps1): the real app, maximized on the
    /// work area of CLIENT01, in both themes. These are the images that docs\images holds, taken from the light theme.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LabPageOfImage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start-sidebar-default"] = "app-packager",
        ["core-add-app-step1"] = "add-from-web",
        ["core-add-file-pick"] = "add-from-file",
        ["core-settings-top"] = "settings",
        ["core-workbench-application"] = "workbench",
        ["tools-site-hygiene-options"] = "tools-site-hygiene-options",
        ["tools-site-hygiene-summary"] = "tools-site-hygiene-summary",
        ["tools-site-hygiene-relationships"] = "tools-site-hygiene-relationships",
        ["tools-site-hygiene-findings"] = "tools-site-hygiene-findings",
        ["tools-installer-analysis-strings"] = "tools-installer-analysis-strings",
        ["tools-installer-analysis-registry"] = "tools-installer-analysis-registry",
        ["tools-installer-analysis-file-browser"] = "tools-installer-analysis-file-browser",
        ["tools-installer-analysis-payload"] = "tools-installer-analysis-payload",
        ["tools-installer-analysis-msi-properties"] = "tools-installer-analysis-msi-properties",
        ["tools-installer-analysis-overview"] = "tools-installer-analysis-overview",
    };

    /// <summary>Every image that a check run produces: the lab capture run, and the render of the pages in the tests.</summary>
    public static IEnumerable<string> Ids => LabPageOfImage.Keys.Union(PageOfImage.Keys);
}
