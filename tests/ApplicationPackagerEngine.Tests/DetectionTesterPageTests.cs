using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using DataGrid = System.Windows.Controls.DataGrid;
using TabControl = System.Windows.Controls.TabControl;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The compiled Detection Tester page in a real window, over fake services.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DetectionTesterPageTests
{
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Lines { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Lines.Add(message);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static T ById<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);

    /// <summary>
    /// Runs the test with the theme and control dictionaries merged into an application object,
    /// as App.xaml merges them, and removes the application object afterwards.
    /// </summary>
    private static void WithPage(ApplicationTheme theme, Action<Window, DetectionTesterPage, DetectionTesterHarness> test)
    {
        StaTestHelper.Run(() =>
        {
            Assert.Null(Application.Current);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var errors = new BindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
                application.Resources.MergedDictionaries.Add(new ControlsDictionary());
                var harness = new DetectionTesterHarness();
                harness.Applications.Applications.Add(new InstalledApplication("7-Zip", "Igor Pavlov", "24.09", "20261001", "HKLM 64-bit",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip", "u.exe", "u.exe /S", @"C:\Program Files\7-Zip"));
                harness.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "SOFTWARE" }));
                var viewModel = harness.Create();
                var page = new DetectionTesterPage(viewModel);
                var window = new Window
                {
                    Width = 1200, Height = 900, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false,
                    WindowStyle = WindowStyle.None, Content = page
                };
                window.Show();
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                    var opened = page.OnNavigatedToAsync();
                    StaTestHelper.PumpUntil(() => opened.IsCompleted);
                    window.UpdateLayout();
                    StaTestHelper.Flush();
                    test(window, page, harness);
                    Assert.Empty(errors.Lines);
                }
                finally { window.Close(); }
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
                application.Resources = new ResourceDictionary();
                var statics = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                typeof(Application).GetField("_appInstance", statics)!.SetValue(null, null);
                typeof(Application).GetField("_appCreatedInThisAppDomain", statics)!.SetValue(null, false);
            }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void BothTabsRenderInTheTheme(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var tabs = ById<TabControl>(page, "tabDetectionTester");
            Assert.Equal(["Detection", "Installed Applications"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));

            Assert.Contains(Descendants(page).OfType<TextBlock>(), t => t.Text == "Detection Tester");
            var types = ById<System.Windows.Controls.ComboBox>(page, "cboClauseType");
            Assert.Equal(4, types.Items.Count);
            Assert.True(ById<FrameworkElement>(page, "pnlRegistryValue").IsVisible);
            var history = ById<DataGrid>(page, "dgHistory");
            Assert.Equal(["Time", "Kind", "Description", "Negative", "Result"], history.Columns.Select(c => (string)c.Header));
            Assert.Single(history.Items);
            Assert.True(ById<System.Windows.Controls.Button>(page, "btnRun").IsEnabled);

            var viewModel = (ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel)page.DataContext;
            viewModel.SelectedClauseType = "Script";
            StaTestHelper.Flush();
            Assert.True(ById<FrameworkElement>(page, "pnlScript").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "pnlRegistryValue").IsVisible);
            Assert.Contains(Descendants(page).OfType<TextBlock>(), t => t.IsVisible && t.Text == "This script runs on this computer as the current user.");
            Assert.True(ById<System.Windows.Controls.TextBox>(page, "txtScript").FontSize >= 14);

            tabs.SelectedIndex = 1;
            StaTestHelper.PumpUntil(() => Descendants(page).OfType<DataGrid>()
                .SingleOrDefault(g => AutomationProperties.GetAutomationId(g) == "dgApplications")?.Items.Count == 1);
            var grid = ById<DataGrid>(page, "dgApplications");
            Assert.Equal(["Name", "Publisher", "Version", "Install date", "Source"], grid.Columns.Select(c => (string)c.Header));
            Assert.True(ById<System.Windows.Controls.TextBox>(page, "txtApplicationFilter").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "pnlApplicationDetails").IsVisible);
            viewModel.SelectedApplication = viewModel.Applications[0];
            StaTestHelper.Flush();
            Assert.True(ById<FrameworkElement>(page, "pnlApplicationDetails").IsVisible);
            Assert.Contains(Descendants(ById<FrameworkElement>(page, "pnlApplicationDetails")).OfType<System.Windows.Controls.TextBox>(),
                t => t.Text == @"C:\Program Files\7-Zip");
            Assert.Equal(1, harness.Applications.Calls);
        });
    }

    [Fact]
    public void AResultShowsItsStateInTheResultPanel()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var viewModel = (ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel)page.DataContext;
            viewModel.SelectedClauseType = "RegistryKey";
            viewModel.KeyPath = "SOFTWARE";
            ById<System.Windows.Controls.Button>(page, "btnRun").Command.Execute(null);
            StaTestHelper.PumpUntil(() => viewModel.ResultState == ApplicationPackagerUI.ViewModels.Pages.DetectionTesterResultState.Passed);
            Assert.Equal("Passed", ById<TextBlock>(page, "txtResultHeadline").Text);
            Assert.Equal(2, ById<DataGrid>(page, "dgHistory").Items.Count);
        });
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DetectionTesterSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dt-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public DetectionTesterSettingsTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, json);
    }

    [Fact]
    public void ASettingsFileFromBeforeThePageLoadsWithoutPageChoices()
    {
        WriteSettings("{ \"FirstRunCompleted\": true, \"SiteCode\": \"LAB\", \"SiteCodeSaved\": true }");
        var settings = new SettingsService().Load();
        Assert.Null(settings.DetectionTester);
        Assert.Equal("LAB", settings.SiteCode);
        Assert.Null(DetectionTesterPreferences.Load(new SettingsService()));
    }

    [Fact]
    public void ThePageChoicesSaveIntoTheSettingsFileAndKeepTheOtherValues()
    {
        WriteSettings("{ \"FirstRunCompleted\": true, \"SiteCode\": \"LAB\", \"SiteCodeSaved\": true, \"Theme\": \"Light\" }");
        var service = new SettingsService();
        DetectionTesterPreferences.Save(service, new DetectionTesterSettings { ClauseType = "Script", Hive = "HKCU", View = "32-bit" });
        var loaded = new SettingsService().Load();
        Assert.Equal("Script", loaded.DetectionTester!.ClauseType);
        Assert.Equal("HKCU", loaded.DetectionTester.Hive);
        Assert.Equal("32-bit", loaded.DetectionTester.View);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal("LAB", loaded.SiteCode);
    }

    [Fact]
    public void AnUnreadableSettingsFileIsNotOverwritten()
    {
        WriteSettings("{ this is not json");
        DetectionTesterPreferences.Save(new SettingsService(), new DetectionTesterSettings { ClauseType = "Script" });
        Assert.Equal("{ this is not json", File.ReadAllText(SettingsService.SettingsPath));
    }

    [Fact]
    public void TheApplicationServicesBuildThePageViewModel()
    {
        var services = new ServiceCollection().AddPackagerServices().AddSidebar();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddDetectionTesterPage();
        using var provider = services.BuildServiceProvider();
        var viewModel = provider.GetRequiredService<ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel>();
        Assert.Same(viewModel, provider.GetRequiredService<ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel>());
        Assert.IsType<DetectionTesterPrompts>(provider.GetRequiredService<IDetectionTesterPrompts>());
    }
}
