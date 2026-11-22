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
        ["core-settings-top"] = "core-settings-top",
        ["core-workbench-application"] = "workbench",
        ["core-settings-wsus"] = "core-settings-wsus",
        ["core-settings-tools"] = "core-settings-tools",
        ["core-settings-github-api"] = "core-settings-github-api",
        ["core-settings-intune-cve"] = "core-settings-intune-cve",
        ["core-settings-install-parameters"] = "core-settings-install-parameters",
        ["core-settings-install-parameters-sources"] = "core-settings-install-parameters-sources",
        ["core-settings-defaults"] = "core-settings-defaults",
        ["core-settings-configmgr"] = "core-settings-configmgr",
        ["core-settings-citrix"] = "core-settings-citrix",
        ["core-settings-about"] = "core-settings-about",
        ["core-script-signing-settings"] = "core-script-signing-settings",
        ["core-one-click-settings"] = "core-one-click-settings",
        ["tools-deployment-helper-options"] = "tools-deployment-helper-options",
        ["tools-deployment-helper-deploy"] = "tools-deployment-helper-deploy",
        ["tools-collections-site-queries"] = "tools-collections-site-queries",
        ["tools-collections-templates"] = "tools-collections-templates",
        ["tools-collections-wql"] = "tools-collections-wql",
        ["tools-collections-list"] = "tools-collections-list",
        ["tools-dp-content-content"] = "tools-dp-content-content",
        ["tools-maintenance-windows-templates"] = "tools-maintenance-windows-templates",
        ["tools-maintenance-windows-coverage"] = "tools-maintenance-windows-coverage",
        ["tools-maintenance-windows-list"] = "tools-maintenance-windows-list",
        ["tools-site-hygiene-options"] = "tools-site-hygiene-options",
        ["tools-site-hygiene-summary"] = "tools-site-hygiene-summary",
        ["tools-site-hygiene-relationships"] = "tools-site-hygiene-relationships",
        ["tools-site-hygiene-findings"] = "tools-site-hygiene-findings",
        ["tools-site-hygiene-blockers"] = "tools-site-hygiene-blockers",
        ["tools-installer-analysis-strings"] = "tools-installer-analysis-strings",
        ["tools-installer-analysis-registry"] = "tools-installer-analysis-registry",
        ["tools-installer-analysis-file-browser"] = "tools-installer-analysis-file-browser",
        ["tools-installer-analysis-payload"] = "tools-installer-analysis-payload",
        ["tools-installer-analysis-msi-properties"] = "tools-installer-analysis-msi-properties",
        ["tools-installer-analysis-overview"] = "tools-installer-analysis-overview",
        // The supersedence views of the App Packager page (tools/guide-capture/Capture-PackagesSupersedence.ps1: the real app driven with UI Automation, one theme after the other).
        ["core-packages-context-menu"] = "core-packages-context-menu",
        ["core-packages-supersedence-confirm"] = "core-packages-supersedence-confirm",
        ["core-packages-supersedence-confirm-remember"] = "core-packages-supersedence-confirm-remember",
        ["core-packages-supersedence-log"] = "core-packages-supersedence-log",
        ["tools-settings-supersedence"] = "tools-settings-supersedence",
        ["core-settings-application-settings"] = "core-settings-application-settings",
        ["core-one-click-plan-supersedence"] = "core-one-click-plan-supersedence",
        // The states of the App Packager page and its windows (tools/guide-capture/Capture-PackagesPage.ps1 and Capture-PackagesDialogs.ps1: the real app, a Stage into the staging folder of the run, a
        // Publish of one application that the run removes from the site again, one theme after the other).
        ["packages"] = "packages",
        ["core-packages-list"] = "core-packages-list",
        ["core-packages-select-update"] = "core-packages-select-update",
        ["core-packages-export-check-results"] = "core-packages-export-check-results",
        ["core-packages-buttons-disabled"] = "core-packages-buttons-disabled",
        ["core-packages-operation-history"] = "core-packages-operation-history",
        ["core-packages-review-dialog"] = "core-packages-review-dialog",
        ["core-packages-conflict-dialog"] = "core-packages-conflict-dialog",
        ["core-one-click-plan"] = "core-one-click-plan",
        ["core-stage-result-log"] = "core-stage-result-log",
        ["core-publish-configmgr-log"] = "core-publish-configmgr-log",
        ["start-sidebar-context-menu"] = "start-sidebar-context-menu",
        ["tools-pivots-pivot"] = "pivots-and-scripts",
        // The Discover prompt and the CVE views (Capture-CoreWindows, with a throw-away Mozilla Firefox 140.0 application on the site that the caller removes).
        ["core-discover-prompt"] = "core-discover-prompt",
        ["core-packages-cve-columns"] = "core-packages-cve-columns",
        ["core-cve-window"] = "core-cve-window",
        ["tools-compliance-catalog"] = "tools-compliance-catalog",
        ["tools-compliance-deploy"] = "tools-compliance-deploy",
        // The Site Hygiene import window and Live views (Capture-ToolsHygiene).
        ["tools-site-hygiene-import"] = "tools-site-hygiene-import",
        ["tools-site-hygiene-live-deployments"] = "tools-site-hygiene-live-deployments",
        ["tools-site-hygiene-live-clients"] = "tools-site-hygiene-live-clients",
        ["tools-site-hygiene-live-site-health"] = "tools-site-hygiene-live-site-health",
        ["tools-site-hygiene-trends"] = "tools-site-hygiene-trends",
        ["tools-site-hygiene-live-options"] = "tools-site-hygiene-live-options",
        // The update window, the command-line help and the first package (Capture-UpdateAndCli, Capture-FirstPackage).
        ["core-update-available"] = "core-update-available",
        ["core-update-ready"] = "core-update-ready",
        ["core-cli-help"] = "core-cli-help",
        ["start-first-package-end-to-end"] = "start-first-package-end-to-end",
        // The pending images that the lab capture runs of tools/guide-capture (Capture-AddAndWorkbench, Capture-Setup, Capture-CoreWindows, Capture-Tools) produce.
        ["core-add-app-step2-test"] = "core-add-app-step2-test",
        ["core-add-app-step3"] = "core-add-app-step3",
        ["core-add-app-step4-analysis"] = "core-add-app-step4-analysis",
        ["core-add-app-step5-install"] = "core-add-app-step5-install",
        ["core-add-app-step6-detection"] = "core-add-app-step6-detection",
        ["core-add-app-step7-review"] = "core-add-app-step7-review",
        ["core-add-file-review"] = "core-add-file-review",
        ["core-workbench-install"] = "core-workbench-install",
        ["core-workbench-detection"] = "core-workbench-detection",
        ["core-workbench-requirements"] = "core-workbench-requirements",
        ["core-workbench-timing"] = "core-workbench-timing",
        ["core-workbench-source-files"] = "core-workbench-source-files",
        ["core-workbench-review"] = "core-workbench-review",
        ["core-workbench-bundle-menu"] = "core-workbench-bundle-menu",
        ["core-catalog"] = "core-catalog",
        ["core-convert-window"] = "core-convert-window",
        ["core-discover-settings"] = "core-discover-settings",
        ["start-setup-systems"] = "start-setup-systems",
        ["start-setup-connection"] = "start-setup-connection",
        ["tools-detection-tester-registry"] = "tools-detection-tester-registry",
        ["tools-detection-tester-script"] = "tools-detection-tester-script",
        ["tools-detection-tester-installed"] = "tools-detection-tester-installed",
        ["tools-collections-editor"] = "tools-collections-editor",
        ["tools-collections-remove-plan"] = "tools-collections-remove-plan",
        ["tools-maintenance-windows-editor"] = "tools-maintenance-windows-editor",
        ["tools-maintenance-windows-plan"] = "tools-maintenance-windows-plan",
        ["tools-dp-content-dps"] = "tools-dp-content-dps",
        ["tools-pivots-options"] = "tools-pivots-options",
        ["tools-pivots-site-scripts"] = "tools-pivots-site-scripts",
        ["tools-compliance-live"] = "tools-compliance-live",
        ["tools-compliance-drift"] = "tools-compliance-drift",
    };

    /// <summary>Every image that a check run produces: the lab capture run, and the render of the pages in the tests.</summary>
    public static IEnumerable<string> Ids => LabPageOfImage.Keys.Union(PageOfImage.Keys);
}
