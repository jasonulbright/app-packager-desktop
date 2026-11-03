using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Tests.Collections;
using ApplicationPackagerEngine.Tests.Deployments;
using ApplicationPackagerEngine.Tests.DpContent;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerEngine.Tests.SiteHygiene;
using ApplicationPackagerEngine.Tests.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The main grids of the picker, plan, WSUS, Discovery and Workbench windows at the height of the minimum audit window (720 px).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WindowGridRowsMoreTests
{
    private static void Open(Window window)
    {
        window.Width = Math.Max(MinimumSizeRows.Width, window.MinWidth);
        window.Height = Math.Min(window.Height, MinimumSizeRows.Height);
        Show(window);
    }

    private static string Id(int i) => "LABR" + i.ToString("0000", CultureInfo.InvariantCulture);

    private static List<CollectionRow> Collections(int count) => Enumerable.Range(1, count).Select(i => new CollectionRow(CollectionId: Id(i), Name: $"Finance devices of building {i}",
        FolderPath: @"Deploy\Finance", LimitingCollectionId: "SMS00001", LimitingCollectionName: "All Systems", MemberCount: 9, IsBuiltIn: false,
        RefreshType: "Periodic", Comment: "", DirectRuleCount: 0, QueryRuleCount: 0, IncludeRuleCount: 0, ExcludeRuleCount: 0)).ToList();

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDiscoveryGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var viewModel = new DiscoveryDialogViewModel(Enumerable.Range(1, 8).Select(i => new DiscoveryCandidate("id" + i, "contoso" + i, "Contoso", "Contoso Application " + i, 100 - i)), "8 apps found.");
            var window = new DiscoveryWindow(viewModel);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.Add("gridDiscover", (DataGrid)window.FindName("gridDiscover"));
                rows.AssertEnough($"Discovery {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheWsusUpdatesGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var client = new FakeWsusAdminClient
            {
                Updates = Enumerable.Range(1, 8).Select(i => new WsusPublishedUpdate(Guid.NewGuid().ToString(), "Contoso " + i, "Update", "AppPackager:catalog:x/default", "1.0", DateTimeOffset.Now,
                    "2026-10-03 10:00", "", false, false, false)).ToList()
            };
            var viewModel = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01", ApprovalGroup = "Pilot" }, new FakeWsusPrompts());
            var window = new WsusUpdatesWindow(viewModel, _ => { });
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                Settle(window);
                rows.Add("gridUpdates", (DataGrid)window.FindName("gridUpdates"));
                rows.AssertEnough($"WSUS updates {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheCatalogImportBoxesShowAtLeastThreeLines(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var lines = string.Join(Environment.NewLine, Enumerable.Range(1, 12).Select(i => Guid.NewGuid() + "  imported"));
            var viewModel = new WsusCatalogImportViewModel(new FakeWsusAdminClient(), new WsusSettings { ServerName = "wsus01" }, new FakeWsusPrompts()) { InputText = lines };
            var window = new WsusCatalogImportWindow(viewModel);
            Open(window);
            try
            {
                typeof(WsusCatalogImportViewModel).GetProperty(nameof(WsusCatalogImportViewModel.ResultsText))!.SetValue(viewModel, lines);
                Settle(window);
                foreach (var name in new[] { "txtInput", "txtResults" })
                {
                    var box = (TextBox)window.FindName(name);
                    var visible = box.GetLastVisibleLineIndex() - box.GetFirstVisibleLineIndex() + 1;
                    Assert.True(visible >= MinimumSizeRows.Least, $"Catalog import {theme}: {name} shows {visible} lines.");
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDpContentPairsGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = DpContentPageHarness.Large(4, 3);
            var picker = new DpContentPairsViewModel("Remove from DP", DpContentManagerViewModel.PickIntro(DpContentChangeKind.Remove), h.Service.Pairs.ToList());
            var window = new DpContentPairsWindow(picker);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.Add("gridPairs", (DataGrid)window.FindName("gridPairs"));
                rows.AssertEnough($"DP content pairs {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheCollectionPickerGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var picker = new CollectionPickerViewModel("Apply template All workstations", "Check the collections.", "Show plan", Collections(8), _ => Task.FromResult(true));
            var window = new CollectionPickerWindow(picker);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.Add("dgPickerTargets", ById<DataGrid>(window, "dgPickerTargets"));
                rows.AssertEnough($"Collection picker {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheHygieneFindingsPickerGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = SiteHygienePageHarness.Large(8, null);
            var picker = new HygieneFindingsPickerViewModel("Delete application", SiteHygieneViewModel.PickIntro(HygieneFixKind.RemoveApplication), h.Service.Findings);
            var window = new HygieneFindingsPickerWindow(picker);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.Add("gridHygienePick", (DataGrid)window.FindName("gridHygienePick"));
                rows.AssertEnough($"Hygiene findings picker {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheRingCollectionPickerGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var picker = new RingCollectionPickerViewModel("Check the collections", Collections(8), null, multiSelect: true);
            var window = new RingCollectionPickerWindow(picker);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.Add("dgPickerCollections", (DataGrid)window.FindName("dgPickerCollections"));
                rows.AssertEnough($"Ring collection picker {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheRingPlanEditorGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = new DeploymentPageHarness();
            h.Service.Add(DeploymentObjectType.Application, "16777399", "Contoso Application", "2026.10.20.1234");
            var definitions = Enumerable.Range(1, 8).Select(i => FakeRingService.Ring($"Wave {i}", Id(i), $"Finance devices {i}", i == 1 ? "Available" : "Required", i, i == 1 ? null : i + 2, threshold: 90, dpGroup: "Branch DPs")).ToList();
            h.Rings.Plans.Add(new RingPlan(Name: "Finance rollout", Description: "Waves.", HoldLaterRings: true, TimeBasedOn: "Utc", Rings: definitions, IsSeed: false));
            var plan = h.Rings.Plans.Single(p => p.Name.StartsWith("Finance", StringComparison.Ordinal));
            var editor = new RingPlanEditorViewModel(plan, false, h.Rings.Plans, Collections(3), (_, _) => Task.FromResult(true), _ => null);
            var window = new RingPlanEditorWindow(editor);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.Add("dgEditorRings", (DataGrid)window.FindName("dgEditorRings"));
                rows.AssertEnough($"Ring plan editor {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheApplyTemplateGridShowsAtLeastThreeWholeRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var dialog = new MaintenanceApplyTemplateViewModel(harness.Store.Templates, harness.Service.Collections, new Dictionary<string, int>(), _ => Task.FromResult(true));
            var window = new MaintenanceApplyTemplateWindow(dialog);
            Open(window);
            try
            {
                var rows = new MinimumSizeRows();
                rows.AddPadded("dgApplyTargets", ById<DataGrid>(window, "dgApplyTargets"));
                rows.AssertEnough($"Apply template {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 0)]
    [InlineData(ApplicationTheme.Dark, 0)]
    [InlineData(ApplicationTheme.Light, 2)]
    [InlineData(ApplicationTheme.Dark, 2)]
    [InlineData(ApplicationTheme.Light, 3)]
    [InlineData(ApplicationTheme.Dark, 3)]
    [InlineData(ApplicationTheme.Light, 5)]
    [InlineData(ApplicationTheme.Dark, 5)]
    [InlineData(ApplicationTheme.Light, 6)]
    [InlineData(ApplicationTheme.Dark, 6)]
    public void TheWorkbenchGridsShowAtLeastThreeWholeRows(ApplicationTheme theme, int section)
    {
        WithApplication(theme, () =>
        {
            var viewModel = new WorkbenchWindowViewModel(null!, null!, null!, null!);
            for (var i = 1; i <= 8; i++)
            {
                viewModel.ByoSources.Add(new ByoSourceRow(i, $"contoso-{i}.msi", "1.0." + i, "msi", "x64", new string('a', 64), "2026-10-0" + (i % 9 + 1), ""));
                viewModel.Clauses.Add(DetectionClauseRow.From(new WorkbenchDetectionClause("1", "Registry", @"HKLM\SOFTWARE\Contoso " + i, "Exists", "")));
                viewModel.Requirements.Add(RequirementOperationRow.From(new WorkbenchRequirementRow("Add", "rule" + i, "Operating system is Windows 11", "", "*")));
                viewModel.Variants.Add(VariantOverrideRow.From("-x" + i, "setup.exe /s", "", "10", "30"));
                viewModel.SourceFiles.Add(SourceFileRow.From(new WorkbenchSourceFile("asset" + i, "Bundled " + i, "dest" + i, new string('b', 64), 1000 * i, false)));
                viewModel.PriorBuilds.Add(new PriorBuildRow("build-2026-10-0" + i, "1.0." + i, "r" + i, "Sealed"));
            }
            viewModel.DiffRows = new System.Collections.ObjectModel.ObservableCollection<WorkbenchDiffRow>(Enumerable.Range(1, 8).Select(i => new WorkbenchDiffRow("Field " + i, "Default", "Custom")));
            viewModel.Findings = new System.Collections.ObjectModel.ObservableCollection<WorkbenchFinding>(Enumerable.Range(1, 8).Select(i => new WorkbenchFinding("Warning", "Code" + i, "A message of finding " + i)));
            viewModel.SelectedSectionIndex = section;

            var window = (WorkbenchWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkbenchWindow));
            typeof(WorkbenchWindow).BaseType!.GetConstructor(Type.EmptyTypes)!.Invoke(window, null);
            window.InitializeComponent();
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = viewModel;
            var host = new Window
            {
                Width = Math.Max(MinimumSizeRows.Width, window.MinWidth),
                Height = Math.Max(MinimumSizeRows.Height, window.MinHeight),
                WindowStyle = WindowStyle.None,
                Content = content,
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush")
            };
            Show(host);
            try
            {
                var names = section switch
                {
                    0 => new[] { "gridByoSources" },
                    2 => new[] { "dgDetClauses" },
                    3 => new[] { "dgRequirements", "dgVariants" },
                    5 => new[] { "dgSourceFiles" },
                    _ => new[] { "dgDiff", "dgFindings", "dgBuilds" }
                };
                var rows = new MinimumSizeRows();
                foreach (var name in names) rows.Add(name, (DataGrid)window.FindName(name));
                rows.AssertEnough($"Workbench section {section} {theme}");
            }
            finally { host.Close(); }
        });
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WindowStatusColumnTests
{
    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheOutcomeColumnsOfTheOperationHistoryFollowTheOutcomeAndReadInBothStates(ApplicationTheme theme)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apstatuscol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            WithApplication(theme, () =>
            {
                var inventory = new ApplicationPackagerEngine.Services.ApplicationInventoryService(Path.Combine(folder, "history.db"));
                var now = DateTimeOffset.Now;
                var identity = new InventoryApplicationIdentity("app1", "catalog:app1", "default", 2);
                for (var i = 0; i < 6; i++)
                {
                    var outcome = i % 3 == 1 ? InventoryOutcome.Failed : InventoryOutcome.Succeeded;
                    var operationId = "op" + i;
                    inventory.BeginOperation(new InventoryOperation(operationId, "Publish", "app1", identity, "ConfigMgr", now.AddMinutes(-i)));
                    foreach (var action in new[] { "Publish", "Stage", "Retire" })
                        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app1", identity, InventoryEnvironmentIdentity.ConfigMgr("ABC", "CM01"), action, "1.0." + i,
                            action == "Retire" ? InventoryOutcome.Failed : outcome, "obj" + i, now.AddMinutes(-i), "build-" + i, operationId));
                    inventory.CompleteOperation(operationId, outcome, now.AddMinutes(-i + 1));
                }
                var window = new OperationHistoryWindow(new ApplicationPackagerUI.Models.PackagerInfo { Uid = "app1", Application = "Contoso Application" }, inventory);
                window.Width = 1180;
                window.Height = 760;
                Show(window);
                try
                {
                    var operations = StatusColumnKit.Assert(window, (DataGrid)window.FindName("OperationsGrid"), $"OperationsGrid-{theme}", "Outcome");
                    var events = StatusColumnKit.Assert(window, (DataGrid)window.FindName("EventsGrid"), $"EventsGrid-{theme}", "Outcome");
                    Assert.Contains(ApplicationPackagerUI.Helpers.OutcomeKind.Success, operations);
                    Assert.Contains(ApplicationPackagerUI.Helpers.OutcomeKind.Critical, operations);
                    Assert.Contains(ApplicationPackagerUI.Helpers.OutcomeKind.Success, events);
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not delete the temp folder '{folder}': {ex.Message}");
            }
        }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheStatusAndStateColumnsOfTheDpPairsFollowTheOutcomeAndReadInBothStates(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = ApplicationPackagerEngine.Tests.DpContent.DpContentPageHarness.Large(4, 3);
            var picker = new DpContentPairsViewModel("Remove from DP", DpContentManagerViewModel.PickIntro(DpContentChangeKind.Remove), h.Service.Pairs.ToList());
            var window = new DpContentPairsWindow(picker);
            window.Width = 1024;
            window.Height = 720;
            Show(window);
            try
            {
                var grid = (DataGrid)window.FindName("gridPairs");
                var seen = StatusColumnKit.Assert(window, grid, $"gridPairs-{theme}", "Status", "State");
                Assert.NotEmpty(seen);
            }
            finally { window.Close(); }
        });
    }
}
