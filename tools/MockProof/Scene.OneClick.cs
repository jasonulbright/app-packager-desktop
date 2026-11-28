using System.IO;
using System.Windows.Media.Imaging;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace MockProof;

/// <summary>The One Click plan windows of the mock: a run that publishes, and a run that only checks.</summary>
internal sealed partial class Scene
{
    private sealed class NoRunner : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct) => Task.FromResult(false);
    }

    private static OneClickPlanViewModel OneClickModel(string action, params OneClickPlanItem[] rows)
    {
        var model = new OneClickPlanViewModel(new OneClickPlanContext
        {
            Apps = [],
            Settings = () => new OneClickPlanSettings(),
            Action = action,
            ReportFolder = Path.GetTempPath(),
            PlannedSite = "MCM|cm01.contoso.com",
            SiteText = "ConfigMgr site MCM",
            SystemsText = "In use: ConfigMgr. Not in use: Intune, WSUS.",
        }, new NoRunner());
        foreach (var row in rows) model.Rows.Add(row);
        return model;
    }

    private static OneClickPlanItem Item(string name, string version, bool include, bool configMgr, string last, string planned, string reason = "") =>
        new() { Key = name, Application = name, Version = version, Include = include, ConfigMgr = configMgr, LastPublished = last, Planned = planned, Reason = reason };

    [Screen("one-click")]
    private Task<BitmapSource> OneClickPlan() => OverMain(new OneClickPlanWindow(OneClickModel(OneClickActionNames.StageAndPackage,
        Item("Audacity (x64)", "4.0.2", true, true, "ConfigMgr 3.7.9", "Create application and deployment type"),
        Item("Notepad++ (x64)", "8.9.9.1", true, true, "", "Create application and deployment type"),
        Item("7-Zip (x64)", "26.06", false, true, "ConfigMgr 26.06", "", "no update")), Path.GetTempPath()));

    [Screen("check-only")]
    private Task<BitmapSource> CheckOnly() => OverMain(new OneClickPlanWindow(OneClickModel(OneClickActionNames.Report,
        Item(".NET 8 Desktop Runtime (x64)", "8.0.34", false, false, "", "", "Checked within the 7-day cadence"),
        Item(".NET 8 Desktop Runtime (x86)", "8.0.34", true, false, "", "Check", "Due for a check"),
        Item("7-Zip (x64)", "26.06", false, false, "ConfigMgr 26.06", "", "Checked within the 7-day cadence")), Path.GetTempPath()));
}
