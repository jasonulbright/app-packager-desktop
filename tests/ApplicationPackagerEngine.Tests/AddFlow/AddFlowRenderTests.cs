using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Control = System.Windows.Controls.Control;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests.AddFlow;

/// <summary>The Add from file and Add from web pages, the result panel and the Open definition list in a real window, in both themes and two sizes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AddFlowRenderTests : IDisposable
{
    /// <summary>The width of the closed sidebar; the page gets the rest of the window.</summary>
    private const double ClosedSidebar = 48;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-add-render-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public AddFlowRenderTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    public static TheoryData<ApplicationTheme, int, int> Sizes => new()
    {
        { ApplicationTheme.Light, 1440, 900 },
        { ApplicationTheme.Dark, 1440, 900 },
        { ApplicationTheme.Light, 900, 600 },
        { ApplicationTheme.Dark, 900, 600 }
    };

    private static Window Host(FrameworkElement content, int width, int height)
    {
        var window = new Window
        {
            Width = width - ClosedSidebar,
            Height = height - ClosedSidebar,
            WindowStyle = WindowStyle.None,
            Content = content,
            Background = (Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush")
        };
        Show(window);
        return window;
    }

    private static T Id<T>(DependencyObject root, string id) where T : DependencyObject => ById<T>(root, id);

    private static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush, exactMatch: false).Color;

    /// <summary>No visible button, check box or evidence marker is arranged narrower than its measured content.</summary>
    private static void AssertNothingClipped(DependencyObject root, string where)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>().Where(e => e.IsVisible && e is ButtonBase or CheckBox or Border { Child: TextBlock }))
        {
            var name = AutomationProperties.GetAutomationId(element) is { Length: > 0 } id ? id : element.GetType().Name;
            var width = element.ActualWidth + element.Margin.Left + element.Margin.Right;
            Assert.True(width >= element.DesiredSize.Width - 0.5,
                $"{where}: {name} is {element.ActualWidth:0.0} px wide; it needs {element.DesiredSize.Width - element.Margin.Left - element.Margin.Right:0.0} px.");
        }
        foreach (var text in Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible && t.TextTrimming == TextTrimming.None && t.TextWrapping == TextWrapping.NoWrap && t.Text.Length > 0))
        {
            var needed = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
            var visible = LayoutInformation.GetLayoutClip(text) is { } clip ? clip.Bounds.Width : text.ActualWidth;
            Assert.True(visible >= needed - 0.5, $"{where}: the text '{text.Text}' shows {visible:0.0} px of {needed:0.0} px.");
        }
    }

    /// <summary>Every visible control the user can operate has an automation name.</summary>
    private static void AssertAutomationNames(DependencyObject root, string where)
    {
        foreach (var control in Descendants(root).OfType<Control>().Where(c => c.IsVisible && c.Focusable && c is ButtonBase or TextBox or ComboBox or DataGrid))
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(control);
            Assert.False(string.IsNullOrWhiteSpace(peer?.GetName()), $"{where}: a {control.GetType().Name} with id '{AutomationProperties.GetAutomationId(control)}' has no automation name.");
        }
    }

    private static void AssertTitleUsesThePrimaryTextBrush(Window window, TextBlock title, string where)
    {
        var primary = ColorOf(System.Windows.Application.Current.FindResource("TextFillColorPrimaryBrush"));
        Assert.Equal(primary, ColorOf(title.Foreground));
        var background = ColorOf(System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush"));
        var pixels = Pixels(window, title, where);
        var contrast = pixels.Max(p => Contrast(p, background));
        Assert.True(contrast >= 4.5, $"{where}: the title has a contrast of {contrast:0.00} on {background}.");
    }

    private static List<string> TabOrder(Window window, UIElement start, int count)
    {
        window.Activate();
        Assert.True(start.Focus(), "The first element takes no focus.");
        var order = new List<string> { AutomationProperties.GetAutomationId(start) };
        for (var i = 1; i < count; i++)
        {
            ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            order.Add(Keyboard.FocusedElement is DependencyObject d ? AutomationProperties.GetAutomationId(d) is { Length: > 0 } id ? id : AutomationProperties.GetName(d) : "");
        }
        return order;
    }

    private (AddFileViewModel ViewModel, FakeAddService Service, FakeResultActions Actions) AddFile()
    {
        var service = new FakeAddService();
        var actions = new FakeResultActions();
        return (new AddFileViewModel(service: service, prompts: new FakeAddPrompts(), results: new AddResultViewModel(actions)) { DefinitionsRoot = _root }, service, actions);
    }

    private static void ToReview(AddFileViewModel viewModel, FakeAddService service, AddAnalysisResult result)
    {
        int index;
        lock (service.Analyses) index = service.Analyses.Count;
        viewModel.AnalyzeFileCommand.Execute(result.FilePath);
        service.WaitForAnalysis(index).Result.SetResult(result);
        StaTestHelper.PumpUntil(() => viewModel.Stage == AddFileStage.Review);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AddFromFileFitsInEveryStage(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var (viewModel, service, _) = AddFile();
            var page = new AddFilePage(viewModel);
            var window = Host(page, width, height);
            var where = $"Add from file {theme} {width}x{height}";
            try
            {
                AssertTitleUsesThePrimaryTextBrush(window, Id<TextBlock>(page, "txtAddFileHeader"), where);
                var open = Id<Button>(page, "btnAddFileOpen");
                AssertInside(window, open, window.ActualHeight, "btnAddFileOpen");
                Assert.True(open.IsDefault);
                AssertNothingClipped(page, where + " pick");
                AssertAutomationNames(page, where + " pick");

                ToReview(viewModel, service, FakeAddService.Result(needsReview: true, reason: "No silent switch could be derived.", argumentsEvidence: Evidence.None));
                Settle(window);
                var save = Id<Button>(page, "btnAddFileSave");
                AssertInside(window, save, window.ActualHeight, "btnAddFileSave");
                AssertInside(window, Id<CheckBox>(page, "chkAddFileAcceptPredicted"), window.ActualHeight, "chkAddFileAcceptPredicted");
                Assert.True(save.IsDefault);
                Assert.True(Id<Border>(page, "pnlAddFileReviewWarning").IsVisible);
                AssertNothingClipped(page, where + " review");
                AssertAutomationNames(page, where + " review");
                Assert.Contains(Descendants(page).OfType<Border>(), b => b.IsVisible && AutomationProperties.GetName(b) == "Uninstall arguments: Predicted, not verified");

                viewModel.PredictedAccepted = true;
                var run = viewModel.SaveCommand.ExecuteAsync(null);
                StaTestHelper.PumpUntil(() => run.IsCompleted);
                Settle(window);
                foreach (var id in new[] { "btnAddResultWorkbench", "btnAddResultStage", "btnAddResultDone" })
                    AssertInside(window, Id<Button>(page, id), window.ActualHeight, id);
                Assert.True(Id<Button>(page, "btnAddResultWorkbench").IsDefault);
                Assert.False(Id<Button>(page, "btnAddResultDone").IsDefault);
                Assert.True(Id<Border>(page, "pnlAddResultReason").IsVisible);
                AssertNothingClipped(page, where + " result");
                AssertAutomationNames(page, where + " result");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void ThePackageAndPatchSectionsFitAndTheTrustWarningShows(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var (viewModel, service, _) = AddFile();
            var page = new AddFilePage(viewModel);
            var window = Host(page, width, height);
            var where = $"Add from file package {theme} {width}x{height}";
            try
            {
                var package = new PackageFacts(IdentityName: "Contoso.App", Publisher: "CN=Contoso Ltd, O=Contoso Ltd, L=Redmond, S=Washington, C=US", Version: "1.2.3.0",
                    Architecture: "x64", Dependencies: ["Microsoft.VCLibs.140.00.UWPDesktop", "Microsoft.WindowsAppRuntime.1.5"], IsBundle: true, Trust: PackageTrust.Unsigned)
                { TrustWarning = "The package is not signed. Windows installs it only when sideloading of unsigned packages is allowed on the target." };
                ToReview(viewModel, service, FakeAddService.Result(path: @"C:\in\app.msixbundle") with { InstallerType = "MSIX", Package = package });
                Settle(window);
                var warning = Id<Border>(page, "pnlAddFileTrustWarning");
                Assert.True(warning.IsVisible, $"{where}: the trust warning is hidden.");
                var scroller = Id<ScrollViewer>(page, "scrollAddFileReview");
                var top = warning.TransformToAncestor(scroller).Transform(new System.Windows.Point(0, 0)).Y;
                Assert.True(top + warning.ActualHeight <= scroller.ViewportHeight + 0.5, $"{where}: the trust warning is below the first screen of the review.");
                Assert.True(warning.ActualWidth <= scroller.ViewportWidth + 0.5, $"{where}: the trust warning is wider than the review.");
                AssertNothingClipped(page, where);
                AssertAutomationNames(page, where);

                ToReview(viewModel, service, FakeAddService.Result(path: @"C:\in\fix.msp") with
                {
                    InstallerType = "MSP",
                    Patch = new PatchFacts(PatchCode: "{AAAAAAAA-0000-0000-0000-000000000001}", Title: "Contoso fix 1", TargetProductCodes: ["{BBBBBBBB-0000-0000-0000-000000000002}"],
                        TargetProductName: "Contoso App", PostPatchVersion: "1.2.4", BaseProductWarning: "Install Contoso App before this patch.")
                });
                Settle(window);
                Assert.True(Id<Border>(page, "pnlAddFilePatchWarning").IsVisible);
                Assert.False(Id<StackPanel>(page, "pnlAddFilePackage").IsVisible);
                AssertNothingClipped(page, where + " patch");
                AssertAutomationNames(page, where + " patch");
            }
            finally { window.Close(); }
        });
    }
    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheResultPanelMakesDoneTheDefaultForAConfidentResult(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var (viewModel, service, _) = AddFile();
            var page = new AddFilePage(viewModel);
            var window = Host(page, 1440, 900);
            try
            {
                ToReview(viewModel, service, FakeAddService.Result());
                viewModel.PredictedAccepted = true;
                var run = viewModel.SaveCommand.ExecuteAsync(null);
                StaTestHelper.PumpUntil(() => run.IsCompleted);
                Settle(window);
                Assert.True(Id<Button>(page, "btnAddResultDone").IsDefault);
                Assert.False(Id<Button>(page, "btnAddResultWorkbench").IsDefault);
                Assert.False(Id<Border>(page, "pnlAddResultReason").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheKeyboardReachesTheDropZoneThroughThePickerAndTheReviewInOrder(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var (viewModel, service, _) = AddFile();
            var page = new AddFilePage(viewModel);
            var window = Host(page, 1440, 900);
            try
            {
                window.Activate();
                page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                Assert.Equal("btnAddFileOpen", AutomationProperties.GetAutomationId((DependencyObject)Keyboard.FocusedElement));

                ToReview(viewModel, service, FakeAddService.Result());
                Settle(window);
                var order = TabOrder(window, Id<TextBox>(page, "txtAddFileVendor"), 7);
                Assert.Equal(["txtAddFileVendor", "txtAddFileName", "txtAddFileVersion", "txtAddFileInstallArguments", "txtAddFileUninstallArguments",
                    "cboAddFileInstallContext", "cboDetectionKind"], order);
            }
            finally { window.Close(); }
        });
    }

    private AddAppViewModel AddWeb(FakeAddService service)
    {
        var settings = new SettingsService();
        settings.ApplyRunOverrides(s => s.DownloadRoot = Path.Combine(_root, "downloads"));
        var viewModel = new AddAppViewModel(settingsService: settings, snackbarService: new FakeSnackbar(), service: service, results: new AddResultViewModel(new FakeResultActions()))
        {
            DefinitionsRoot = Path.Combine(_root, "definitions"),
            Scrape = (_, _) => Task.FromResult<(string?, string?)>(("1.2.3", "1.2.3"))
        };
        viewModel.Vendor = "Contoso";
        viewModel.ApplicationName = "Contoso App " + Guid.NewGuid().ToString("N")[..6];
        viewModel.ScraperUrl = "https://example.com";
        viewModel.ScraperPattern = "(.*)";
        viewModel.DownloadUrl = "https://example.com/app.msi";
        return viewModel;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AddFromWebFitsOnEveryStep(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var service = new FakeAddService();
            var viewModel = AddWeb(service);
            var page = new AddAppPage(viewModel);
            var window = Host(page, width, height);
            var where = $"Add from web {theme} {width}x{height}";
            try
            {
                for (var step = 1; step <= AddAppViewModel.StepCount; step++)
                {
                    if (step == AddAppViewModel.AnalysisStep)
                    {
                        viewModel.AnalyzeCommand.Execute(null);
                        service.WaitForAnalysis(0).Result.SetResult(FakeAddService.Result(needsReview: true, reason: "Detection needs review."));
                        StaTestHelper.PumpUntil(() => viewModel.HasAnalysis);
                    }
                    Settle(window);
                    var stepWhere = $"{where} step {step}";
                    var next = step < AddAppViewModel.StepCount ? Id<Button>(page, "btnAddWebNext") : Id<Button>(page, "btnAddWebSave");
                    AssertInside(window, next, window.ActualHeight, stepWhere);
                    Assert.True(next.IsDefault, $"{stepWhere}: the forward button is not the default button.");
                    AssertNothingClipped(page, stepWhere);
                    AssertAutomationNames(page, stepWhere);
                    if (step < AddAppViewModel.StepCount) viewModel.NextStepCommand.Execute(null);
                }

                Assert.True(Id<Border>(page, "pnlAddWebDetectionWarning").IsVisible);
                var review = Id<Wpf.Ui.Controls.TextBox>(page, "txtAddWebReview");
                WholeLineKit.AssertWholeLines(review, where);
                var split = Assert.IsType<Wpf.Ui.Controls.SplitButton>(Id<Button>(page, "btnAddWebSave"));
                var menu = Assert.IsType<ContextMenu>(split.Flyout);
                Assert.Equal(["Save and open in Workbench"], menu.Items.OfType<MenuItem>().Select(m => (string)m.Header));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheAddFromWebTitleUsesThePrimaryTextColor(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var page = new AddAppPage(AddWeb(new FakeAddService()));
            var window = Host(page, 1440, 900);
            try
            {
                var title = Id<TextBlock>(page, "txtAddWebHeader");
                Assert.Equal("Add from web", title.Text);
                AssertTitleUsesThePrimaryTextBrush(window, title, $"Add from web title {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void NoAddFromWebStringNamesTheOldPage()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var page = new AddAppPage(AddWeb(new FakeAddService()));
            var window = Host(page, 1440, 900);
            try
            {
                foreach (var text in Descendants(page).OfType<FrameworkElement>().Select(e => (e.ToolTip as string ?? "") + " " + (e is TextBlock t ? t.Text : "")))
                {
                    Assert.DoesNotContain("Add New Application", text);
                    Assert.DoesNotContain("Add App", text);
                    Assert.DoesNotContain("Applications page", text);
                }
            }
            finally { window.Close(); }
        });
    }

    private static readonly WorkbenchApplicationItem[] Definitions =
    [
        new() { ApplicationId = "catalog:putty", Origin = ApplicationOrigin.Catalog, DisplayName = "PuTTY", Publisher = "Simon Tatham", IsJsonDefinition = true, HasRemoteSource = true },
        new() { ApplicationId = "custom:a1", Origin = ApplicationOrigin.Custom, DisplayName = "Contoso Dropped Application", Publisher = "Contoso", IsJsonDefinition = true },
        new() { ApplicationId = "custom:b2", Origin = ApplicationOrigin.Custom, DisplayName = "Fabrikam Web Application", Publisher = "Fabrikam", IsJsonDefinition = true, HasRemoteSource = true },
        new() { ApplicationId = "byo:0123", Origin = ApplicationOrigin.Byo, DisplayName = "Saved Installer", Publisher = "Northwind" }
    ];

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheOpenDefinitionListFitsItsColumnsAndShowsTheSelection(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            var panel = new OpenDefinitionPanel { DataContext = new OpenDefinitionViewModel(Definitions), Width = 640, Height = 420 };
            var window = new Window { SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, Content = panel,
                Background = (Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush") };
            Show(window);
            try
            {
                var grid = Id<DataGrid>(panel, "gridOpenDefinition");
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, $"Open definition {theme}");
                WholeLineKit.AssertWholeRows(grid, $"Open definition {theme}");
                AssertReadableRowsAndVisibleSelection(window, grid, $"Open definition {theme}");
                AssertAutomationNames(panel, $"Open definition {theme}");
                Assert.Equal(["Shipped", "Local file", "From web", "Local file"], grid.Items.Cast<OpenDefinitionRow>().Select(r => r.SourceText));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheWorkbenchHeaderFitsTheOpenDefinitionButtonAtTheSmallestWindow(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var window = (WorkbenchWindow)RuntimeHelpers.GetUninitializedObject(typeof(WorkbenchWindow));
            typeof(WorkbenchWindow).BaseType!.GetConstructor(Type.EmptyTypes)!.Invoke(window, null);
            window.InitializeComponent();
            // The window starts the view model when it renders; its content is shown in a plain window of the same smallest size.
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Window
            {
                Width = window.MinWidth,
                Height = window.MinHeight,
                WindowStyle = WindowStyle.None,
                Content = content,
                Background = (Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush")
            };
            Show(host);
            try
            {
                var open = Id<Wpf.Ui.Controls.Button>(content, "btnOpenDefinition");
                Assert.Equal("Open definition...", open.Content);
                var rename = (FrameworkElement)window.FindName("btnProfileRename");
                var build = (FrameworkElement)window.FindName("cboBuild");
                Rect Bounds(FrameworkElement e) => e.TransformToAncestor(host).TransformBounds(new Rect(e.RenderSize));
                Assert.True(Bounds(open).Right <= Bounds(rename).Left + 0.5, "Open definition overlaps Rename.");
                Assert.True(Bounds(build).Right <= host.ActualWidth + 0.5, $"The Build list ends at {Bounds(build).Right:0} px in a {host.ActualWidth:0} px window.");
                Assert.True(build.ActualWidth >= build.MinWidth - 0.5);
                AssertNothingClipped((FrameworkElement)((FrameworkElement)open.Parent).Parent, $"Workbench header {theme}");
            }
            finally { host.Close(); }
        });
    }
}
