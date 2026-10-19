using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ApplicationPackagerEngine.Tests.Collections;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using ListBox = System.Windows.Controls.ListBox;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>The Compliance section and its dialogs in real windows, over fake services, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ComplianceRenderTests
{
    /// <summary>The main window gives this width to the navigation pane and its border; the page gets the rest.</summary>
    private const double NavigationPaneWidth = 230;

    private static void WithSection(ApplicationTheme theme, Action<Window, CollectionCompliancePage, ComplianceViewModel, ComplianceHarness> test,
        double width = 1440 - NavigationPaneWidth, double height = 900 - 48)
    {
        WithApplication(theme, () =>
        {
            var harness = new ComplianceHarness();
            var collections = new CollectionComplianceHarness { Saved = new ApplicationPackagerUI.Models.CollectionComplianceSettings { Section = CollectionComplianceViewModel.ComplianceSection } };
            var compliance = harness.Create();
            var page = new CollectionCompliancePage(collections.Create(), compliance);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Settle(window);
                test(window, page, compliance, harness);
            }
            finally { window.Close(); }
        });
    }

    private static void ShowTab(Window window, ComplianceViewModel viewModel, int tab)
    {
        viewModel.SelectedTabIndex = tab;
        Run(viewModel.CurrentTab.LoadTask);
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSectionSwitchShowsTheFourComplianceTabs(ApplicationTheme theme)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            Assert.True(ById<System.Windows.Controls.RadioButton>(page, "rbSectionCompliance").IsChecked);
            Assert.False(((FrameworkElement)page.FindName("gridCollectionsSection")).IsVisible);
            var tabs = ById<TabControl>(page, "tabCompliance");
            Assert.Equal(["Catalog", "Live CI/CB", "Editor", "Auditing"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));
            Assert.Equal(3, ById<DataGrid>(page, "dgCatalog").Items.Count);
            Assert.Equal(0, harness.Service.ItemCalls);

            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            Assert.Equal(2, ById<DataGrid>(page, "dgLiveItems").Items.Count);
            Assert.Equal(3, ById<DataGrid>(page, "dgDeployments").Items.Count);
            ShowTab(window, viewModel, ComplianceViewModel.EditorTab);
            Assert.Equal(2, ById<ListBox>(page, "lstEditorItems").Items.Count);
            ShowTab(window, viewModel, ComplianceViewModel.AuditingTab);
            Assert.Equal(3, ById<DataGrid>(page, "dgDrift").Items.Count);

            ((CollectionComplianceViewModel)page.DataContext).IsCollectionsSection = true;
            Settle(window);
            Assert.True(((FrameworkElement)page.FindName("gridCollectionsSection")).IsVisible);
            Assert.False(ById<FrameworkElement>(page, "viewCompliance").IsVisible);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - 48)]
    public void EveryGridShowsAllItsColumnsAt1440Pixels(ApplicationTheme theme, double width)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            viewModel.Catalog.SelectedRow = viewModel.Catalog.Rows[0];
            Settle(window);
            var catalog = ById<DataGrid>(page, "dgCatalog");
            AssertResizableReadableColumns(catalog);
            AssertNoSidewaysScroll(catalog, "Catalog");

            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            foreach (var id in new[] { "dgLiveItems", "dgLiveBaselines", "dgDeployments" })
            {
                var grid = ById<DataGrid>(page, id);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, id);
            }

            ShowTab(window, viewModel, ComplianceViewModel.EditorTab);
            viewModel.Editor.SelectedItem = viewModel.Editor.Items.Single(i => i.CiId == "CI-SMB");
            Run(viewModel.Editor.SettingsLoad);
            Settle(window);
            var settings = ById<DataGrid>(page, "dgSettings");
            AssertResizableReadableColumns(settings);
            AssertNoSidewaysScroll(settings, "Settings");

            ShowTab(window, viewModel, ComplianceViewModel.AuditingTab);
            var drift = ById<DataGrid>(page, "dgDrift");
            AssertResizableReadableColumns(drift);
            AssertNoSidewaysScroll(drift, "Drift");
            viewModel.Auditing.SelectedTabIndex = ComplianceAuditingViewModel.ComplianceTab;
            Settle(window);
            var counts = ById<DataGrid>(page, "dgComplianceCounts");
            AssertResizableReadableColumns(counts);
            AssertNoSidewaysScroll(counts, "Compliance");
        }, width);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryGridShowsReadableRowsAndAVisibleSelection(ApplicationTheme theme)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgCatalog"), $"Catalog-{theme}");

            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgLiveItems"), $"Items-{theme}");
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgLiveBaselines"), $"Baselines-{theme}");
            viewModel.Live.SelectedBaseline = null;
            Settle(window);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgDeployments"), $"Deployments-{theme}");

            ShowTab(window, viewModel, ComplianceViewModel.EditorTab);
            AssertReadableListAndVisibleSelection(window, ById<ListBox>(page, "lstEditorItems"), $"EditorItems-{theme}");
            Run(viewModel.Editor.SettingsLoad);
            viewModel.Editor.SelectedItem = viewModel.Editor.Items.Single(i => i.CiId == "CI-SMB");
            Run(viewModel.Editor.SettingsLoad);
            Settle(window);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgSettings"), $"Settings-{theme}");

            ShowTab(window, viewModel, ComplianceViewModel.AuditingTab);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgDrift"), $"Drift-{theme}");
            viewModel.Auditing.SelectedTabIndex = ComplianceAuditingViewModel.ComplianceTab;
            Settle(window);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgComplianceCounts"), $"ComplianceCounts-{theme}");

            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments);
            Run(viewModel.Live.DeleteDeploymentsCommand.ExecuteAsync(null));
            Settle(window);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgComplianceResults"), $"Results-{theme}");
            Assert.True(ById<FrameworkElement>(page, "btnComplianceUndo").IsVisible);
        });
    }

    [Fact]
    public void ScreenReadersHearPlainNamesForRowsAndItems()
    {
        WithSection(ApplicationTheme.Dark, (window, page, viewModel, harness) =>
        {
            static List<string> Names(DataGrid grid) =>
                UIElementAutomationPeer.CreatePeerForElement(grid).GetChildren().OfType<DataGridItemAutomationPeer>().Select(p => p.GetName()).ToList();
            Assert.Equal("Autorun Disable, Compliance, High", Names(ById<DataGrid>(page, "dgCatalog"))[0]);
            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            Assert.Contains("SMB Signing Required on Servers (LAB00010), Detect only", Names(ById<DataGrid>(page, "dgDeployments")));
            ShowTab(window, viewModel, ComplianceViewModel.EditorTab);
            var items = UIElementAutomationPeer.CreatePeerForElement(ById<ListBox>(page, "lstEditorItems")).GetChildren().Select(p => p.GetName()).ToList();
            Assert.Equal(["SMB Signing (CI-SMB)", "TLS Legacy (CI-TLS)"], items);
            Assert.All(items, n => Assert.DoesNotContain("{", n));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheCatalogDetailsCollapseAndComeBack(ApplicationTheme theme)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            viewModel.Catalog.SelectedRow = viewModel.Catalog.Rows.Single(r => r.Entry.Id == "specexec-mitigations");
            Settle(window);
            Assert.True(ById<FrameworkElement>(page, "pnlCatalogDetails").IsVisible);
            var grid = ById<DataGrid>(page, "dgCatalog");
            var withDetails = grid.ActualWidth;
            viewModel.Catalog.ShowDetails = false;
            Settle(window);
            Assert.False(ById<FrameworkElement>(page, "pnlCatalogDetails").IsVisible);
            Assert.True(grid.ActualWidth > withDetails + 300);
            viewModel.Catalog.ShowDetails = true;
            Settle(window);
            Assert.Equal(withDetails, grid.ActualWidth, 1);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDeployFormRendersEveryParameterAndTheDependencyPanel(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new ComplianceHarness();
            var form = new CatalogDeployViewModel(harness.SpecExec, harness.Settings, harness.Collections.Collections, _ => Task.FromResult(true),
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
            var window = new CatalogDeployWindow(form);
            Show(window);
            try
            {
                Assert.Equal(form.Title, window.Title);
                Assert.True(ById<System.Windows.Controls.TextBox>(window, "txtParamSiteCode").IsReadOnly);
                Assert.Equal("LAB", ById<System.Windows.Controls.TextBox>(window, "txtParamSiteCode").Text);
                Assert.True(ById<FrameworkElement>(window, "btnPickHtEnabledCollectionName").IsVisible);
                Assert.True(ById<System.Windows.Controls.CheckBox>(window, "chkParamDetectOnly").IsChecked);
                Assert.Equal(form.ModeText, ById<TextBlock>(window, "txtDeployMode").Text);
                Assert.Equal(2, ById<ItemsControl>(window, "lstDependencies").Items.Count);
                Assert.True(ById<Button>(window, "btnDeployShowPlan").IsDefault);
                var footer = ById<FrameworkElement>(window, "pnlDeployFooter");
                AssertInside(window, ById<FrameworkElement>(window, "btnDeployShowPlan"), window.ActualHeight, "btnDeployShowPlan");
                Pixels(window, footer, $"Deploy-{theme}");
                Assert.All(new[] { "txtParamSiteServer", "txtParamHtEnabledCollectionName", "chkParamSkipDeploy", "chkParamDetectOnly", "btnDeployShowPlan" },
                    id => Assert.True(ById<Control>(window, id).Focusable, id));
            }
            finally { window.Close(); }
        });
    }

    public static TheoryData<CiSettingEditorMode, string, ApplicationTheme> EditorCases()
    {
        var data = new TheoryData<CiSettingEditorMode, string, ApplicationTheme>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            data.Add(CiSettingEditorMode.Add, "Registry", theme);
            data.Add(CiSettingEditorMode.Edit, "Script", theme);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EditorCases))]
    public void TheSettingDialogShowsTheInputsOfItsKind(CiSettingEditorMode mode, string kind, ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new ComplianceHarness();
            var editor = new CiSettingEditorViewModel(mode, "SMB Signing (CI-SMB)", _ => Task.FromResult(true), mode == CiSettingEditorMode.Edit ? harness.SmbScript : null);
            if (mode == CiSettingEditorMode.Add) editor.Kind = kind;
            var window = new CiSettingEditorWindow(editor);
            Show(window);
            try
            {
                Assert.Equal(kind == "Registry", ById<FrameworkElement>(window, "txtSettingKey").IsVisible);
                Assert.Equal(kind == "Script", ById<FrameworkElement>(window, "txtSettingDiscovery").IsVisible);
                Assert.Equal(mode == CiSettingEditorMode.Add, ById<Control>(window, "cboSettingDataType").IsEnabled);
                Assert.Equal(mode == CiSettingEditorMode.Edit, ById<FrameworkElement>(window, "txtSettingDataTypeNote").IsVisible);
                if (kind == "Script")
                {
                    var script = ById<System.Windows.Controls.TextBox>(window, "txtSettingDiscovery");
                    Assert.Equal(harness.SmbScript.DiscoveryScript, script.Text);
                    Assert.True(script.AcceptsReturn);
                    Assert.False(script.AcceptsTab);
                }
                editor.ShowMessage(CiSettingEditorViewModel.ExpectedRequired);
                Settle(window);
                Assert.Equal(CiSettingEditorViewModel.ExpectedRequired, ById<TextBlock>(window, "txtSettingMessage").Text);
                Pixels(window, ById<FrameworkElement>(window, "pnlSettingFooter"), $"Setting-{mode}-{theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePlanWindowShowsALongObjectAndTheRemediationCheckUncut(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new ComplianceHarness();
            var change = ApplicationPackagerUI.ViewModels.Pages.ComplianceRequests.Deploy(harness.SpecExec, new Dictionary<string, string>(), ["LAB00012"], true);
            var targets = harness.Collections.Collections.Where(c => c.CollectionId == "LAB00012").ToList();
            var (plan, _) = ComplianceCatalogViewModel.BuildDeployPlan(harness.SpecExec with { DisplayName = "CPU Speculative Execution Mitigations Intel and AMD Bundled Hyper-Threading Variants" },
                change, targets, true, false);
            var viewModel = new ChangePlanViewModel(plan!.Plan, (c, p, ct) => Task.FromResult(new ChangePlanResult([])), (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(viewModel);
            Show(window);
            try
            {
                var grid = ById<DataGrid>(window, "gridChangePlan");
                var cells = Descendants(Row(grid, 0)).OfType<DataGridCell>().ToList();
                var objectCell = cells.Single(c => Equals(c.Column.Header, "Object"));
                var text = Descendants(objectCell).OfType<TextBlock>().First();
                Assert.Equal(plan.Plan.Items[0].Target, text.ToolTip);
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.True(text.ActualWidth <= objectCell.ActualWidth + 0.5, $"The object text is {text.ActualWidth:0} px in a {objectCell.ActualWidth:0} px cell.");
                var check = ById<System.Windows.Controls.CheckBox>(window, "chkChangePlanExtra");
                Assert.True(check.IsVisible);
                Assert.Equal(plan.Plan.ExtraConfirmation, System.Windows.Automation.AutomationProperties.GetName(check));
                Assert.Contains(Descendants(check).OfType<TextBlock>(), t => t.Text == plan.Plan.ExtraConfirmation && t.IsVisible);
                AssertInside(window, check, ById<FrameworkElement>(window, "btnChangePlanConfirm").TranslatePoint(new System.Windows.Point(0, 0), window).Y, "chkChangePlanExtra");
                Assert.False(ById<Button>(window, "btnChangePlanConfirm").IsEnabled);
                check.IsChecked = true;
                Settle(window);
                Assert.True(ById<Button>(window, "btnChangePlanConfirm").IsEnabled);
                Pixels(window, grid, $"DeployPlan-{theme}");
            }
            finally { window.Close(); }
        });
    }
}
