using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Point = System.Windows.Point;

namespace MockProof;

/// <summary>
/// The windows that the mock draws over the main window. Each image is the main window, dimmed, with the window of the app drawn at its center.
/// The mock draws the other layout of the main window behind these windows, so the proof of these screens compares the window only.
/// </summary>
internal sealed partial class Scene
{
    private const string ConfigMgr = OneClickDestinations.ConfigMgr, Intune = OneClickDestinations.Intune, Wsus = OneClickDestinations.Wsus;

    private async Task<BitmapSource> OverMain(Window window, Func<Task>? prepare = null)
    {
        ShowPackages();
        window.Owner = _main;
        // The sizes that the window asks for, kept before the display limits them.
        var width = window.Width;
        var height = window.Height;
        window.Show();
        try
        {
            await FitWindowAsync(window, width, height);
            if (prepare is not null) await prepare();
            await SettleAsync();
            window.UpdateLayout();
            var main = Capture(_main);
            var front = Capture(window, false);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(main, new Rect(0, 0, main.PixelWidth, main.PixelHeight));
                var left = (Program.Width - front.PixelWidth) / 2;
                var top = (Program.Height - front.PixelHeight + 1) / 2;
                context.DrawImage(front, new Rect(left, top, front.PixelWidth, front.PixelHeight));
                Console.WriteLine($"window {left},{top},{front.PixelWidth},{front.PixelHeight}");
            }
            var result = new RenderTargetBitmap(Program.Width, Program.Height, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            return result;
        }
        finally { window.Close(); }
    }

    /// <summary>The content of a window gets the size that the window asks for, also where the display gives the window a frame.</summary>
    private static async Task FitWindowAsync(Window window, double width, double height)
    {
        // The same size rules as the main window: a display smaller than a window limits it.
        Size(window, (int)Math.Round(width), (int)Math.Round(height));
        for (var i = 0; i < 4; i++)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            var dx = width - root.ActualWidth;
            var dy = height - root.ActualHeight;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
            Size(window, (int)Math.Round(window.Width + dx), (int)Math.Round(window.Height + dy), false);
        }
    }
    private static PublishPlan Plan(bool several)
    {
        var settings = new ApplicationPackagerUI.Models.AppSettings { SiteCode = "MCM", FileShareRoot = @"\\cm01.contoso.com\ContentShare$" };
        var audacity = new PublishPlanApp("mock0", "Audacity (x64)", "4.0.2", false)
        {
            Site = ConfigMgrFact.Found("3.7.9"),
            Detection = "Product version 4.0.2 or later",
            ContentSource = @"\\cm01.contoso.com\ContentShare$\Audacity",
            CheckedAt = new DateTime(2026, 11, 24, 8, 10, 0),
        };
        var notepad = new PublishPlanApp("mock1", "Notepad++ (x64)", "8.9.9.1", false) { CheckedAt = new DateTime(2026, 11, 24, 8, 10, 0) };
        string[] systems = several ? [ConfigMgr, Intune, Wsus] : [ConfigMgr];
        PublishPlanRow Row(PublishPlanApp app, bool wsusHeld) => new(app, systems.Select(s => s == Wsus && wsusHeld
            ? new PublishPlanCell(s, PublishCellKind.Skip, "Skip: version already published")
            : new PublishPlanCell(s, PublishCellKind.Create, s == Intune ? PublishPlan.IntuneAction : s == Wsus ? PublishPlan.WsusAction : PublishPlan.ConfigMgrAction)).ToList());
        var rows = several ? new List<PublishPlanRow> { Row(audacity, false), Row(notepad, true) } : [Row(audacity, false)];
        var plan = new PublishPlan(systems, rows, []) { Site = "MCM|cm01.contoso.com" };
        return plan with { Cards = PublishCards.For(plan, settings) };
    }

    private static PublishReviewViewModel Review(bool several) => new(Plan(several), "")
    {
        Mark = name => name.StartsWith("Audacity", StringComparison.Ordinal) ? "AU" : "NP",
        WsusName = "Lab server",
    };

    [Screen("publish")]
    private Task<BitmapSource> Publish() => OverMain(new PublishReviewWindow(Review(false)));

    [Screen("multi-publish")]
    private Task<BitmapSource> MultiPublish() => OverMain(new PublishReviewWindow(Review(true)));

    private static PublishDestinationResult Done(string system, bool created = true) =>
        new(system, PublishDestinationKind.Published, "", "") { Created = created };

    private static Dictionary<string, string> MockVersions => new() { ["mock0"] = "4.0.2", ["mock1"] = "8.9.9.1", ["mock4"] = "26.06" };

    private static string MockMark(string name) =>
        name.StartsWith("Audacity", StringComparison.Ordinal) ? "AU" : name.StartsWith("Notepad", StringComparison.Ordinal) ? "NP" : "7Z";

    private static string Report()
    {
        var path = Path.Combine(Path.GetTempPath(), "mockproof-report.md");
        File.WriteAllText(path, "report");
        return path;
    }

    [Screen("publish-result")]
    private Task<BitmapSource> PublishResult()
    {
        var rows = new List<(string, PublishRowResult)> { ("Audacity (x64)", new PublishRowResult("mock0", "4.0.2", [Done(ConfigMgr)])) };
        return OverMain(new PublishResultsWindow(new PublishResultsViewModel(rows, false, [ConfigMgr])
        {
            SiteCode = "MCM", RunNote = "sample run", Versions = MockVersions, Mark = MockMark, ReportPath = Report(),
            WithoutDeployment = new HashSet<string> { "mock0" }, KeptNotice = "Audacity 3.7.9 was kept. Supersedence is off.",
            ShowLogs = _ => { }, ShowHistory = (_, _) => { },
        }));
    }

    [Screen("multi-result")]
    private Task<BitmapSource> MultiResult()
    {
        var rows = new List<(string, PublishRowResult)>
        {
            ("Audacity (x64)", new PublishRowResult("mock0", "4.0.2", [Done(ConfigMgr), Done(Intune), Done(Wsus)])),
            ("Notepad++ (x64)", new PublishRowResult("mock1", "8.9.9.1",
            [
                Done(ConfigMgr),
                new PublishDestinationResult(Intune, PublishDestinationKind.Failed, "content upload", "The upload of the content failed."),
                new PublishDestinationResult(Wsus, PublishDestinationKind.Skipped, "", "This version is already published."),
            ])),
        };
        return OverMain(new PublishResultsWindow(new PublishResultsViewModel(rows, false, [ConfigMgr, Intune, Wsus])
        {
            SiteCode = "MCM", Versions = MockVersions, Mark = MockMark, ReportPath = Report(),
            SystemNames = new Dictionary<string, string> { [Wsus] = "Lab server" },
            ShowLogs = _ => { }, ShowHistory = (_, _) => { },
        }));
    }

    [Screen("results")]
    private Task<BitmapSource> OneClickResults()
    {
        var rows = new List<(string, PublishRowResult)>
        {
            ("Audacity (x64)", new PublishRowResult("mock0", "4.0.2", [Done(ConfigMgr)])),
            ("Notepad++ (x64)", new PublishRowResult("mock1", "8.9.9.1",
                [new PublishDestinationResult(ConfigMgr, PublishDestinationKind.Failed, "stage", "Installer download failed. Check the vendor source before you retry.")])),
            ("7-Zip (x64)", new PublishRowResult("mock4", "26.06",
                [new PublishDestinationResult(ConfigMgr, PublishDestinationKind.Skipped, "", "The latest version is already published.")])),
        };
        return OverMain(new PublishResultsWindow(new PublishResultsViewModel(rows, false, [ConfigMgr])
        {
            IsOneClick = true, SiteCode = "MCM", RunNote = "sample run \u00b7 24 Nov 2026, 08:18", Versions = MockVersions, Mark = MockMark,
            ReportPath = Report(), WithoutDeployment = new HashSet<string> { "mock0" }, ShowLogs = _ => { }, ShowRunHistory = _ => { },
        }));
    }
}
