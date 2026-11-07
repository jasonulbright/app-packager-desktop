using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The main grids of the stand-alone windows, shown at the height of the minimum audit window (720 px) with a width of at least 1024 px.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WindowGridRowsTests
{
    private static void Open(Window window)
    {
        window.Width = Math.Max(MinimumSizeRows.Width, window.MinWidth);
        window.Height = Math.Min(window.Height, MinimumSizeRows.Height);
        Show(window);
    }

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheCveWindowGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var row = new PackagerInfo { Uid = "app1", Application = "Contoso Application", Vendor = "Contoso" };
            var viewModel = new CveWindowViewModel(row, null);
            for (var i = 1; i <= 8; i++)
                viewModel.Rows.Add(new CveWindowRow($"CVE-2026-{1000 + i}", "9.8 CRITICAL", "50.0%", i % 2 == 0 ? "Yes" : "No", "1.2." + i, "2026-09-01", "2026-09-02",
                    "A flaw in the update service lets a local user raise rights.", []));
            var packages = (PackagesViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            var window = new CveWindow(viewModel, packages);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.Add("gridCves", (DataGrid)window.FindName("CveGrid"));
                rows.AssertEnough($"CVE window {theme}");
            }
            finally { window.Close(); }
        });
    }

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheOperationHistoryGridsShowAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apwindowrows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            WithApplication(theme, () =>
            {
                var inventory = new ApplicationInventoryService(Path.Combine(folder, "history.db"));
                var window = new OperationHistoryWindow(new PackagerInfo { Uid = "app1", Application = "Contoso Application" }, inventory);
                var now = DateTimeOffset.Now;
                for (var i = 0; i < 8; i++)
                {
                    window.Operations.Add(new InventoryOperationState("op" + i, "Stage", "app1", null, null, "Contoso " + i, now.AddMinutes(-i), now.AddMinutes(-i + 1),
                        i % 3 == 1 ? InventoryOutcome.Failed : InventoryOutcome.Succeeded, "Detail " + i));
                    window.Events.Add(new InventoryEventRecord(i, "ev" + i, "Published", "app1", "Contoso", null, null, "ConfigMgr", "MCM", "Publish", "1.0." + i, null,
                        InventoryOutcome.Succeeded, "obj" + i, null, null, null, now.AddMinutes(-i), "op" + i, "Detail " + i));
                }
                Open(window);
                try
                {
                    var rows = new MinimumSizeRows();
                    rows.Add("OperationsGrid", (DataGrid)window.FindName("OperationsGrid"));
                    rows.Add("EventsGrid", (DataGrid)window.FindName("EventsGrid"));
                    rows.AssertEnough($"Operation history {theme}");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class NoRunner : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct) => Task.FromResult(false);
    }

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheOneClickPlanGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apwindowrows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            WithApplication(theme, () =>
            {
                var keys = Enumerable.Range(1, 8).Select(i => $"package-{i}").ToList();
                var viewModel = new OneClickPlanViewModel(new OneClickPlanContext
                {
                    Apps = keys.Select((k, i) => (new OneClickApp(k, "Application " + (i + 1), "Contoso", "1." + i), (PackagerInfo?)new PackagerInfo { Uid = k, Application = "Application " + (i + 1) })).ToList(),
                    Settings = () => new OneClickPlanSettings
                    {
                        Tracked = keys,
                        DefaultDestinations = new DestinationSet(true, true, false),
                        Readiness = OneClickPlanner.Readiness(new PublishReadiness(
                            new SystemsInUse { ConfigMgr = true, Intune = false, Wsus = true }, true, "MCM", @"\server\share", "", "", false, "wsus01")),
                        Scope = new OneClickScopeSettings(WsusApprovalGroup: "Pilot")
                    },
                    Action = OneClickActionNames.StageAndPackage,
                    OnExisting = OneClickOnExisting.Skip,
                    ReportFolder = folder
                }, new NoRunner());
                var window = new OneClickPlanWindow(viewModel, folder);
                Open(window);
                try
                {
                    var rows = new MinimumSizeRows();
                    rows.Add("gridPlan", (DataGrid)window.FindName("gridPlan"));
                    rows.AssertEnough($"One Click plan {theme}");
                }
                finally { window.Close(); }
            });
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }
}
