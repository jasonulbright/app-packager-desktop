using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The one publish button of the Workbench: its state, its route through the publish review and its window at two sizes (overhaul V1, phase 7a).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WorkbenchPublishAllTests
{
    private static PublishReadiness Readiness(bool configMgr, bool intune, bool wsus, bool intuneCredentials = false) => new(
        new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }, true, "MCM", @"\\cm01\share",
        intuneCredentials ? "tenant" : "", intuneCredentials ? "client" : "", intuneCredentials, wsus ? "cm01" : "");

    [Fact]
    public void ThePublishAllButtonFollowsTheSystemsInUseAndTheirReadiness()
    {
        var viewModel = new WorkbenchWindowViewModel(null!, null!, null!, null!);

        viewModel.ApplyPublishState(Readiness(configMgr: true, intune: false, wsus: false));
        Assert.True(viewModel.PublishAllCommand.CanExecute(null));
        Assert.Equal("Publish the selected build to ConfigMgr (Settings, Systems in Use). The publish review opens before anything is published.",
            viewModel.PublishAllToolTip);

        viewModel.ApplyPublishState(Readiness(configMgr: true, intune: true, wsus: false));
        Assert.False(viewModel.PublishAllCommand.CanExecute(null));
        Assert.StartsWith("Publish All: Intune:", viewModel.PublishAllToolTip);

        viewModel.ApplyPublishState(Readiness(configMgr: false, intune: false, wsus: false));
        Assert.False(viewModel.PublishAllCommand.CanExecute(null));
        Assert.StartsWith("Publish All: no system is in use.", viewModel.PublishAllToolTip);

        viewModel.ApplyPublishState(Readiness(configMgr: false, intune: false, wsus: true));
        Assert.True(viewModel.PublishAllCommand.CanExecute(null));
        Assert.Equal("Publish the selected build to WSUS (Settings, Systems in Use). The publish review opens before anything is published.",
            viewModel.PublishAllToolTip);
    }

    [Fact]
    public void TheValidationLineHoldsTheThreeResults()
    {
        var viewModel = new WorkbenchWindowViewModel(null!, null!, null!, null!);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.ChipIntune = "Intune: Ready";

        Assert.Equal("Content build: Not validated · ConfigMgr: Not validated · Intune: Ready", viewModel.ValidationLine);
        Assert.Contains(nameof(WorkbenchWindowViewModel.ValidationLine), changed);
    }

    [Fact]
    public void AHelpTextFromATooltipIsAlwaysAText()
    {
        var converter = ApplicationPackagerUI.Helpers.ToolTipTextConverter.Instance;
        object Convert(object? value) => converter.Convert(value, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("Enter the key.", Convert("Enter the key."));
        Assert.Equal("", Convert(null));
        Assert.Equal("", Convert(new object()));

        // A style applies before a field sets its tooltip, so each such binding needs the converter.
        var root = Path.Combine(ApplicationPackagerEngine.Tests.Compliance.CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");
        var bare = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file).Where(line => line.Contains("AutomationProperties.HelpText", StringComparison.Ordinal)
                    && line.Contains("{Binding ToolTip, RelativeSource={RelativeSource Self}}", StringComparison.Ordinal))
                .Select(line => Path.GetFileName(file) + ": " + line.Trim()))
            .ToList();
        Assert.True(bare.Count == 0, "Help text from a tooltip without the converter: " + string.Join("; ", bare));
    }

    private static readonly string[] Fields =
    [
        "txtDisplayName", "txtPublisher", "txtDescription", "cboTitleMode", "txtProfileName", "cboScriptTarget", "cboInstallMode",
        "txtReturnCodes", "txtScript", "cboDetectionMode", "txtDetKey", "txtDetectScript", "cboReqTemplate", "cboVariantSplit",
        "txtSiteArchGc", "txtEstimatedMinutes", "cboExecContext", "cboApplication", "cboProfile", "cboBuild"
    ];

    [Theory]
    [InlineData(ApplicationTheme.Light, 1370, 730)]
    [InlineData(ApplicationTheme.Dark, 1370, 730)]
    [InlineData(ApplicationTheme.Light, 1024, 700)]
    [InlineData(ApplicationTheme.Dark, 1024, 700)]
    public void TheWorkbenchShowsTheSidePanelAndTheFooter(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var viewModel = new WorkbenchWindowViewModel(null!, null!, null!, null!)
            {
                IdentityText = "Application id catalog:package-review; output folders keep using the packager identity.",
                ProvenanceText = @"Definitions\review.json"
            };
            var window = (WorkbenchWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkbenchWindow));
            typeof(WorkbenchWindow).BaseType!.GetConstructor(Type.EmptyTypes)!.Invoke(window, null);
            window.InitializeComponent();
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = viewModel;
            var host = new Window
            {
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                Content = content,
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush")
            };
            Show(host);
            try
            {
                Settle(host);
                var where = $"Workbench {theme} {width}x{height}";
                T Named<T>(string name) where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));
                var panel = Named<WorkbenchSidePanel>("pnlSidePanel");
                var editor = Named<FrameworkElement>("gridEditor");
                var details = Named<ToggleButton>("btnDetails");
                var line = Named<FrameworkElement>("txtValidationLine");
                var docked = width >= WorkbenchWindow.SidePanelDockWidth;

                Assert.Equal(docked, panel.IsVisible);
                Assert.Equal(!docked, details.IsVisible);
                Assert.Equal(!docked, line.IsVisible);
                Assert.True(editor.IsVisible, $"{where}: the editor is hidden.");

                foreach (var name in new[] { "btnUseDefaults", "btnSave", "btnSaveAs", "btnValidate", "btnStage", "btnPublishAll" })
                    AssertInside(host, Named<FrameworkElement>(name), host.ActualHeight, name);
                Assert.Equal("Publish All", Named<Wpf.Ui.Controls.Button>("btnPublishAll").Content);
                foreach (var gone in new[] { "btnPublishMecm", "btnPublishIntune", "btnPublishWsus" })
                    Assert.Null(window.FindName(gone));

                foreach (var name in Fields)
                {
                    var field = Named<DependencyObject>(name);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(field)), $"{where}: {name} has no automation name.");
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(field)), $"{where}: {name} has no help text.");
                }

                if (!docked)
                {
                    details.IsChecked = true;
                    details.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Settle(host);
                    Assert.True(panel.IsVisible, $"{where}: Details does not show the panel.");
                    Assert.False(editor.IsVisible, $"{where}: the panel covers the editor.");

                    viewModel.SelectedSectionIndex = 1;
                    Settle(host);
                    Assert.False(panel.IsVisible, $"{where}: a selected section does not show its editor.");
                    Assert.True(editor.IsVisible);
                }
            }
            finally { host.Close(); }
        });
    }
}
