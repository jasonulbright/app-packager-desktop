using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The One Click plan window, the help text of the ConfigMgr settings, and a UI Automation client walk of the App Packager page.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PlanGapWindowTests
{
    private sealed class FailingRunner : IOneClickRunner
    {
        public string Comment { get; private set; } = "";

        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct)
        {
            Comment = options.Comment;
            foreach (var row in rows)
            {
                row.Cells.Add(new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Failed, "content", "copy failed"));
                row.Outcome = OneClickOutcomes.Failed;
            }
            return Task.FromResult(false);
        }
    }

    private static OneClickPlanViewModel Plan(IOneClickRunner runner, string folder) => new(new OneClickPlanContext
    {
        Apps = [(new OneClickApp("a", "Audacity", "Contoso", "3.7"), (PackagerInfo?)new PackagerInfo { Uid = "a", Application = "Audacity" })],
        Settings = () => new OneClickPlanSettings
        {
            Tracked = ["a"],
            DefaultDestinations = new DestinationSet(true, false, false),
            Readiness = OneClickPlanner.Readiness(new PublishReadiness(new SystemsInUse { ConfigMgr = true }, true, "MCM", @"\\cm01\share", "", "", false, "")),
            Scope = new OneClickScopeSettings()
        },
        Action = OneClickActionNames.StageAndPackage,
        Comment = "WO-7",
        ReportFolder = folder
    }, runner);

    [Theory]
    [InlineData(ApplicationTheme.Light, 1280, 720)]
    [InlineData(ApplicationTheme.Dark, 1280, 720)]
    [InlineData(ApplicationTheme.Light, 1100, 480)]
    [InlineData(ApplicationTheme.Dark, 1100, 480)]
    public void ThePlanNamesEachBoxWithItsApplicationShowsTheSourceAndTheChangeDetails(ApplicationTheme theme, int width, int height)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apd-oneclick-win-" + Guid.NewGuid().ToString("N"));
        try
        {
            WithApplication(theme, () =>
            {
                var viewModel = Plan(new FailingRunner(), folder);
                var window = new OneClickPlanWindow(viewModel, folder) { Width = width, Height = height };
                Show(window);
                try
                {
                    Settle(window);
                    var row = Assert.Single(viewModel.Rows);
                    Assert.Equal("Default", row.SourceText);
                    var grid = (DataGrid)window.FindName("gridPlan");
                    var names = Descendants(grid).OfType<CheckBox>().Select(AutomationProperties.GetName).ToList();
                    foreach (var name in new[] { "Include Audacity", "ConfigMgr for Audacity", "WSUS for Audacity", "Intune for Audacity" })
                        Assert.Contains(name, names);

                    Assert.Equal("ConfigMgr: application only, no distribution, no deployment", row.ChangeDetails);
                    var toggle = Descendants(grid).OfType<ToggleButton>().Single(t => AutomationProperties.GetAutomationId(t) == "btnChangeDetails");
                    Assert.Equal("Change details for Audacity", AutomationProperties.GetName(toggle));
                    toggle.IsChecked = true;
                    Settle(window);
                    Assert.Contains(Descendants(grid).OfType<TextBlock>(), t => t.IsVisible && t.Text == row.ChangeDetails);
                }
                finally { window.Close(); }
            });
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ARunUsesTheCommentOfThePlanAndItsResultsShowTheFailedCell()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apd-oneclick-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new FailingRunner();
            var viewModel = Plan(runner, folder);
            viewModel.Comment = " WO-8 ";

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.Equal("WO-8", runner.Comment);
            var results = viewModel.BuildResults();
            Assert.Equal("Failed at content", Assert.Single(results.Rows).Cells[0]);
            Assert.Equal("a", results.FirstFailed!.Uid);
            Assert.Equal(viewModel.ReportPath, results.ReportPath);
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryFieldOfTheConfigMgrSettingsHasAHelpText(ApplicationTheme theme)
    {
        PageHeaderRenderTests.With("Settings", theme, () =>
        {
            using var host = new PageHeaderRenderTests();
            var (page, _) = host.Create("Settings");
            var missing = new List<string>();
            var fields = 0;
            void Walk(DependencyObject node)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                {
                    if (child is System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox or CheckBox)
                    {
                        fields++;
                        if (AutomationProperties.GetHelpText(child).Length == 0)
                            missing.Add(child.GetType().Name + " " + AutomationProperties.GetAutomationId(child) + " " + AutomationProperties.GetName(child));
                    }
                    Walk(child);
                }
            }
            Walk((DependencyObject)((FrameworkElement)page).FindName("page_configmgr"));

            Assert.True(fields >= 5, $"Only {fields} fields were found on the ConfigMgr page.");
            Assert.True(missing.Count == 0, "Fields without a help text: " + string.Join(", ", missing));
        });
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    /// <summary>
    /// A UI Automation client on another thread reads the page through the automation tree, as a screen reader does: the grid, the
    /// status tabs and the batch actions are there under their names and control types.
    /// </summary>
    [Fact]
    public void AUiAutomationClientFindsTheGridTheTabsAndTheBatchButtons()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var page = AppPackagerFitTests.Create(5, out var viewModel);
            var window = new Window { Width = 1600, Height = 900, Title = "UIA walk", Content = page };
            Show(window);
            try
            {
                Settle(window);
                var handle = new WindowInteropHelper(window).Handle;
                Assert.True(IsWindow(handle));
                var found = new List<(string Type, string Name, string Id)>();
                Exception? failure = null;
                var frame = new DispatcherFrame();
                var client = new Thread(() =>
                {
                    try
                    {
                        var root = AutomationElement.FromHandle(handle);
                        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                            found.Add((element.Current.ControlType.ProgrammaticName, element.Current.Name, element.Current.AutomationId));
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { frame.Continue = false; }
                }) { IsBackground = true };
                client.SetApartmentState(ApartmentState.MTA);
                // The client calls into this thread, so this thread must pump while it waits; the timer ends a walk that never returns.
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                timeout.Tick += (_, _) => frame.Continue = false;
                timeout.Start();
                client.Start();
                Dispatcher.PushFrame(frame);
                timeout.Stop();

                Assert.True(failure is null, failure?.ToString());
                var list = string.Join("; ", found.Select(f => $"{f.Type} '{f.Name}' {f.Id}"));
                Assert.True(found.Any(f => f.Id == "PackagerGrid" && f.Type == "ControlType.DataGrid" && f.Name == "Applications"), "No grid: " + list);
                Assert.True(found.Any(f => f.Id == "tabStatusAll" && f.Type == "ControlType.RadioButton"), "No status tab: " + list);
                Assert.True(found.Any(f => f.Id == "btnStage" && f.Type == "ControlType.Button" && f.Name == "Stage"), "No Stage button: " + list);
                Assert.True(found.Any(f => f.Id == "btnPublishAll" && f.Type == "ControlType.Button" && f.Name == "Publish All"), "No Publish All button: " + list);
            }
            finally { window.Close(); }
        });
    }
}
