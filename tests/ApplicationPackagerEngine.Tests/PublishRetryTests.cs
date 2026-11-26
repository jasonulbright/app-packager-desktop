using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The retry of a failed destination: where the results offer it, and what stops it before it writes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishRetryTests : IDisposable
{
    private const string BothInUse =
        """{ "FirstRunCompleted": true, "Systems": { "ConfigMgr": true, "Intune": false, "Wsus": true }, "SiteCode": "P01", "FileShareRoot": "\\\\cm01\\share", "Wsus": { "ServerName": "wsus01.contoso.com" } }""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-retry-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public PublishRetryTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        TestCleanup.DeleteDirectory(_root);
    }

    private static PublishDestinationResult Failed(string destination, string step = "") => new(destination, PublishDestinationKind.Failed, step, "it failed");

    [Theory]
    [InlineData("ConfigMgr", "", true)]
    [InlineData("ConfigMgr", "stage", true)]
    [InlineData("ConfigMgr", "error", true)]
    [InlineData("ConfigMgr", "cancel", true)]
    [InlineData("ConfigMgr", "content", false)]
    [InlineData("WSUS", "", true)]
    [InlineData("WSUS", "stage", true)]
    [InlineData("WSUS", "cancel", true)]
    [InlineData("Intune", "", false)]
    [InlineData("Intune", "content upload", false)]
    [InlineData("Intune", "error", false)]
    public void ARetryIsOfferedOnlyWhereTheAppCanVerifyTheDestination(string destination, string step, bool offered)
    {
        var advice = PublishRetry.For(Failed(destination, step))!;
        Assert.Equal(offered, advice.CanRetry);
        if (destination == "Intune") Assert.StartsWith("Verify in Intune before retrying.", advice.Text);
    }

    [Theory]
    [InlineData(PublishDestinationKind.Published)]
    [InlineData(PublishDestinationKind.Skipped)]
    [InlineData(PublishDestinationKind.NotSupported)]
    [InlineData(PublishDestinationKind.Blocked)]
    public void ACellThatDidNotFailHasNoRetry(PublishDestinationKind kind) =>
        Assert.Null(PublishRetry.For(new PublishDestinationResult("ConfigMgr", kind, "", "")));

    [Fact]
    public void AConfigMgrRetryWritesOnlyWhenTheSiteDoesNotHaveTheVersion()
    {
        Assert.Null(PublishRetry.ConfigMgrRefusal(ConfigMgrFact.None, "2.0"));
        Assert.Null(PublishRetry.ConfigMgrRefusal(ConfigMgrFact.Found("1.0"), "2.0"));
        Assert.Contains("would publish it again", PublishRetry.ConfigMgrRefusal(ConfigMgrFact.Found("2.0"), "2.0"));
        Assert.Contains("no version", PublishRetry.ConfigMgrRefusal(ConfigMgrFact.Found(""), "2.0"));
        Assert.Contains("could not be read (offline)", PublishRetry.ConfigMgrRefusal(ConfigMgrFact.Unreadable("offline"), "2.0"));
    }

    private static List<(string Application, PublishRowResult Result)> FailedRun(string destination) =>
    [
        ("Alpha", new PublishRowResult("a", "2.0",
        [
            destination == "ConfigMgr" ? Failed("ConfigMgr") : new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Published, "", ""),
            destination == "WSUS" ? Failed("WSUS") : new PublishDestinationResult("WSUS", PublishDestinationKind.Published, "", "")
        ]))
    ];

    [Fact]
    public void OnlyARunThatCanRetryListsOffersAndAnIntuneOfferCannotBeRequested()
    {
        var rows = new List<(string, PublishRowResult)>
        {
            ("Alpha", new PublishRowResult("a", "2.0", [Failed("ConfigMgr"), Failed("Intune"), Failed("WSUS")]))
        };
        Assert.Empty(new PublishResultsViewModel(rows, false).Retries);

        var results = new PublishResultsViewModel(rows, false, offerRetry: true);
        Assert.Equal([("ConfigMgr", true), ("Intune", false), ("WSUS", true)], results.Retries.Select(r => (r.Destination, r.CanRetry)));
        results.RequestRetry(results.Retries[1]);
        Assert.Null(results.RequestedRetry);
        results.RequestRetry(results.Retries[2]);
        Assert.Equal("Retry WSUS for Alpha", results.RequestedRetry!.ButtonName);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheResultsWindowHasARetryButtonForWsusAndAVerifyTextForIntune(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var rows = new List<(string, PublishRowResult)>
            {
                ("Alpha", new PublishRowResult("a", "2.0", [Failed("Intune", "content upload"), Failed("WSUS")]))
            };
            var viewModel = new PublishResultsViewModel(rows, false, offerRetry: true);
            var window = new PublishResultsWindow(viewModel);
            RenderKit.Show(window);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                RenderKit.Settle(window);
                var buttons = RenderKit.Descendants(window).OfType<ButtonBase>()
                    .Where(b => AutomationProperties.GetAutomationId(b) == "btnResultsRetry" && b.IsVisible).ToList();
                var retry = Assert.Single(buttons);
                Assert.Equal("Retry WSUS for Alpha", AutomationProperties.GetName(retry));
                Assert.True(retry.Focusable && retry.IsTabStop && retry.IsEnabled);
                var texts = RenderKit.Descendants(window).OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains(texts, t => t.StartsWith("Alpha, Intune: Verify in Intune before retrying.", StringComparison.Ordinal));
                Assert.False(RenderKit.ById<TextBlock>(window, "txtResultsRetryNote").IsVisible);
                PageChromeKit.AssertNothingClipped((DependencyObject)window.Content, $"publish results with a retry {theme}");

                retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(closed, "The retry button does not close the results.");
                Assert.Equal("WSUS", viewModel.RequestedRetry!.Destination);
            }
            finally { if (!closed) window.Close(); }
        });
    }

    private static Task ShowResultsAsync(PackagesViewModel viewModel, List<(string, PublishRowResult)> rows, PackagerInfo row)
    {
        var build = Activator.CreateInstance(typeof(PackagesViewModel).GetNestedType("PublishRunBuild", BindingFlags.NonPublic)!,
            new List<PackagerInfo> { row }, null, null, null)!;
        return (Task)typeof(PackagesViewModel).GetMethod("ShowResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [rows, false, new[] { "ConfigMgr", "WSUS" }, build])!;
    }

    private static PackagerInfo Row(string version) => new() { Uid = "a", Application = "Alpha", CMName = "Alpha", Vendor = "Contoso", LatestVersion = version };

    private async Task<(PublishRunKit.Dialogs Dialogs, List<(string, PublishRowResult)> Rows)> Retry(string destination, string versionNow,
        string settingsJson = BothInUse, Action<PublishRunKit.Dialogs>? arrange = null)
    {
        File.WriteAllText(SettingsService.SettingsPath, settingsJson);
        var dialogs = new PublishRunKit.Dialogs();
        arrange?.Invoke(dialogs);
        dialogs.ResultActions.Enqueue(results => results.RequestRetry(results.Retries.Single(r => r.Destination == destination)));
        var row = Row(versionNow);
        var settings = new SettingsService();
        var viewModel = PublishRunKit.Create(settings, dialogs, row);
        PublishRunKit.Set(viewModel, "_mecmQueryService", new MecmQueryService(settings));
        var rows = FailedRun(destination);

        await ShowResultsAsync(viewModel, rows, row);

        Assert.False(viewModel.IsOperationRunning);
        // The results open again after the retry.
        Assert.Equal(2, dialogs.Results.Count);
        return (dialogs, rows);
    }

    [Fact]
    public async Task ARetryOpensTheReviewForTheFailedSystemOnlyAndBackPublishesNothing()
    {
        using var scope = SiteContext.Scope(null, _root);
        var (dialogs, rows) = await Retry("WSUS", "2.0");

        var review = Assert.Single(dialogs.Reviews);
        Assert.Equal(["WSUS"], review.Plan.Systems);
        Assert.Equal("Retry of a failed destination: WSUS only.", review.ScopeText);
        Assert.Contains("did not run. The review closed without a confirmation. Nothing was published.", dialogs.Results[1].RetryNote);
        Assert.Equal(PublishDestinationKind.Failed, rows[0].Item2.For("WSUS")!.Kind);
    }

    [Fact]
    public async Task ARetryDoesNotPublishAnotherVersion()
    {
        using var scope = SiteContext.Scope(null, _root);
        var (dialogs, _) = await Retry("WSUS", "3.0");

        Assert.Empty(dialogs.Reviews);
        Assert.Contains("The failed publish was of version 2.0, and the version is now 3.0.", dialogs.Results[1].RetryNote);
    }

    [Fact]
    public async Task ARetryDoesNotRunForASystemThatIsNoLongerInUse()
    {
        using var scope = SiteContext.Scope(null, _root);
        var (dialogs, _) = await Retry("WSUS", "2.0",
            """{ "FirstRunCompleted": true, "Systems": { "ConfigMgr": true, "Intune": false, "Wsus": false } }""");

        Assert.Empty(dialogs.Reviews);
        Assert.Contains("WSUS is no longer in use", dialogs.Results[1].RetryNote);
    }

    [Fact]
    public async Task AConfigMgrRetryDoesNotRunWhenTheSiteCannotBeRead()
    {
        using var scope = SiteContext.Scope(null, _root);
        // The test computer has no ConfigMgr console and no site, so the read fails.
        var (dialogs, _) = await Retry("ConfigMgr", "2.0");

        Assert.Empty(dialogs.Reviews);
        Assert.Contains("ConfigMgr could not be read", dialogs.Results[1].RetryNote);
    }
}
