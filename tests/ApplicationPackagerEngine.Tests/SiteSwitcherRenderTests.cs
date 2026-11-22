using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The footer site switcher of the real main window and the Add site window (Q-112d): layout in both themes at two window sizes.
/// Every limit comes from the layout or the text measure. Site codes are fictional (AB1); hosts are under example.com.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteSwitcherRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-site-switcher-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SiteSwitcherRenderTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        TestCleanup.DeleteDirectory(_root);
    }

    public static TheoryData<ApplicationTheme, int, int> Cases()
    {
        var data = new TheoryData<ApplicationTheme, int, int>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            foreach (var (width, height) in new[] { (1440, 900), (1024, 720) })
                data.Add(theme, width, height);
        return data;
    }

    private static ServiceProvider MainWindowServices()
    {
        var services = new ServiceCollection().AddPackagerServices().AddSidebar();
        Wpf.Ui.DependencyInjection.ServiceCollectionExtensions.AddNavigationViewPageProvider(services);
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<SiteManager>();
        services.AddSingleton<Wpf.Ui.INavigationService, Wpf.Ui.NavigationService>();
        services.AddSingleton<Wpf.Ui.ISnackbarService, Wpf.Ui.SnackbarService>();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddSingleton<MainWindow>();
        foreach (var type in new[] { typeof(PackagesPage), typeof(AddAppPage), typeof(AddFilePage), typeof(SettingsPage),
                     typeof(DetectionTesterPage), typeof(MaintenanceWindowsPage), typeof(CollectionCompliancePage), typeof(InstallerAnalysisPage),
                     typeof(DeploymentHelperPage), typeof(DpContentManagerPage), typeof(PivotsScriptsPage), typeof(SiteHygienePage) })
            services.AddSingleton(type, _ => new Page { Tag = type });
        return services.BuildServiceProvider();
    }

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFooterSwitcherSitsLowerRightShowsTheWholeSiteTextAndHasATooltip(ApplicationTheme theme, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath,
            $$"""{ "FirstRunCompleted": true, "SiteCode": "AB1", "SiteCodeSaved": true, "ProviderMachine": "cm.ab1.example.com", "WindowWidth": {{width}}, "WindowHeight": {{height}}, "PaneOpen": true }""");
        StaTestHelper.Run(() =>
        {
            using var scope = SiteContext.Scope(null, _root);
            using var provider = MainWindowServices();
            var window = provider.GetRequiredService<MainWindow>();
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
            window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            ApplicationHostService.ShowMainWindow(window);
            RenderKit.Settle(window);
            var where = $"{theme} {width}x{height}";

            var root = (FrameworkElement)window.Content;
            var switcher = RenderKit.ById<Wpf.Ui.Controls.Button>(root, "btnSiteSwitcher");
            var text = RenderKit.ById<TextBlock>(root, "txtSiteFooter");
            var navigation = window.GetNavigation();
            var window2 = Bounds(switcher, root);
            var navBounds = Bounds(navigation, root);

            Assert.Equal("Site AB1 · cm.ab1.example.com", text.Text);
            // Lower right: inside the window, right of the middle, below the navigation view.
            Assert.True(window2.Right <= root.ActualWidth + 0.5, $"{where}: the switcher leaves the window on the right ({window2}).");
            Assert.True(window2.Bottom <= root.ActualHeight + 0.5, $"{where}: the switcher leaves the window at the bottom ({window2}).");
            Assert.True(window2.Left > root.ActualWidth / 2, $"{where}: the switcher is not in the right half ({window2}).");
            Assert.True(window2.Top >= navBounds.Bottom - 0.5, $"{where}: the switcher overlaps the navigation view ({window2} against {navBounds}).");
            // The text shows whole: its box holds its measured width, and it is not trimmed.
            var measured = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            Assert.True(text.ActualWidth + 0.5 >= measured.WidthIncludingTrailingWhitespace, $"{where}: the footer text is cut ({text.ActualWidth} < {measured.WidthIncludingTrailingWhitespace}).");
            // The button holds the text and the chevron with room to spare.
            Assert.True(switcher.ActualWidth > text.ActualWidth + 12, $"{where}: the button is not wider than its text.");
            Assert.False(string.IsNullOrWhiteSpace(switcher.ToolTip as string), $"{where}: the switcher has no tooltip.");
            Assert.Contains("switch", (string)switcher.ToolTip, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Site switcher", System.Windows.Automation.AutomationProperties.GetName(switcher));
            window.Close();
        });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheAddSiteWindowShowsEveryControlWholeWithATooltipAndTheValidationMessage(ApplicationTheme theme, int width, int height)
    {
        _ = (width, height);
        RenderKit.WithApplication(theme, () =>
        {
            var viewModel = new AddSiteViewModel();
            var window = new AddSiteWindow(viewModel);
            RenderKit.Show(window);
            var root = (FrameworkElement)window.Content;
            var where = $"{theme}";

            var code = RenderKit.ById<System.Windows.Controls.TextBox>(root, "txtAddSiteCode");
            var provider = RenderKit.ById<System.Windows.Controls.TextBox>(root, "txtAddSiteProvider");
            var label = RenderKit.ById<System.Windows.Controls.TextBox>(root, "txtAddSiteLabel");
            var add = RenderKit.ById<Wpf.Ui.Controls.Button>(root, "btnAddSiteAccept");
            var cancel = RenderKit.ById<Wpf.Ui.Controls.Button>(root, "btnAddSiteCancel");
            var message = RenderKit.ById<TextBlock>(root, "txtAddSiteMessage");

            foreach (FrameworkElement control in new FrameworkElement[] { code, provider, label, add, cancel })
            {
                var bounds = Bounds(control, root);
                Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0, $"{where}: {control.Name} has no size.");
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= root.ActualWidth + 0.5 && bounds.Bottom <= root.ActualHeight + 0.5, $"{where}: {control.Name} is outside the window ({bounds}).");
                Assert.False(string.IsNullOrWhiteSpace(control.ToolTip as string),
                    $"{where}: {System.Windows.Automation.AutomationProperties.GetAutomationId(control)} has no tooltip.");
            }
            // No two input controls overlap, and the buttons stay right of the message.
            var inputs = new[] { code, provider, label }.Select(c => Bounds(c, root)).OrderBy(r => r.Top).ToList();
            for (var i = 1; i < inputs.Count; i++) Assert.True(inputs[i].Top >= inputs[i - 1].Bottom, $"{where}: two fields overlap.");
            Assert.True(Bounds(add, root).Left > Bounds(message, root).Left, $"{where}: the buttons are left of the message.");
            Assert.True(Bounds(add, root).Right <= Bounds(cancel, root).Left + 0.5 || Bounds(cancel, root).Right <= Bounds(add, root).Left + 0.5, $"{where}: Add site and Cancel overlap.");

            // An invalid code shows a message and keeps the window open; a valid one is accepted in upper case.
            code.Text = "SMS";
            add.Command!.Execute(null);
            RenderKit.Settle(window);
            Assert.False(viewModel.Accepted);
            Assert.Contains("reserved", message.Text);
            Assert.True(message.ActualHeight > 0, $"{where}: the message is not shown.");
            code.Text = "ab1";
            add.Command!.Execute(null);
            Assert.True(viewModel.Accepted);
            Assert.Equal("AB1", viewModel.SiteCode);
        });
    }
}
