using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Publish All with WSUS as the only system in use opens the same review, with the same read before the write.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WsusOnlyPublishReviewTests : IDisposable
{
    private const string WsusOnlySettings =
        """{ "FirstRunCompleted": true, "Systems": { "ConfigMgr": false, "Intune": false, "Wsus": true }, "Wsus": { "ServerName": "wsus01.contoso.com" } }""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-wsus-review-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public WsusOnlyPublishReviewTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        TestCleanup.DeleteDirectory(_root);
    }

    private static PackagerInfo Row() => new() { Uid = "a", Application = "Alpha", Vendor = "Contoso", LatestVersion = "2.0", IsSelected = true };

    [Fact]
    public async Task PublishAllToWsusOnlyOpensTheReviewAndBackPublishesNothing()
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath, WsusOnlySettings);
        var dialogs = new PublishRunKit.Dialogs();
        var row = Row();
        var viewModel = PublishRunKit.Create(new SettingsService(), dialogs, row);

        await PublishRunKit.PublishAllAsync(viewModel);

        var review = Assert.Single(dialogs.Reviews);
        Assert.Equal(["WSUS"], review.Plan.Systems);
        Assert.True(review.CanRun);
        Assert.Equal("Publish 1 application", review.ConfirmText);
        Assert.Empty(dialogs.Results);
        Assert.True(row.IsSelected);
        Assert.False(viewModel.IsOperationRunning);
        Assert.Equal("Publish cancelled. Nothing was published.", viewModel.StatusText);
    }

    [Fact]
    public async Task AChangeAfterTheConfirmOpensTheReviewAgainAndNothingIsPublished()
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath, WsusOnlySettings);
        var dialogs = new PublishRunKit.Dialogs();
        var row = Row();
        // The version changes while the first review is open, as a check that ends in that time would change it.
        dialogs.Answers.Enqueue(_ =>
        {
            row.LatestVersion = "3.0";
            return true;
        });
        var viewModel = PublishRunKit.Create(new SettingsService(), dialogs, row);

        await PublishRunKit.PublishAllAsync(viewModel);

        Assert.Equal(2, dialogs.Reviews.Count);
        Assert.Contains("the version is now 3.0", dialogs.Reviews[1].ChangedText);
        Assert.Empty(dialogs.Results);
        Assert.True(row.IsSelected);
    }

    private static PublishReviewViewModel Review(bool configMgr, bool wsus)
    {
        var readiness = new PublishReadiness(new SystemsInUse { ConfigMgr = configMgr, Intune = false, Wsus = wsus }, true, "MCM", @"\\cm01\share", "", "", false, "wsus01");
        return new PublishReviewViewModel(PublishPlanner.Plan([new PublishPlanApp("a", "Alpha", "2.0", false)], readiness.Systems, readiness), "");
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheWsusOnlyReviewNamesNoConfigMgrReadAndAsksForNoComment(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            foreach (var wsusOnly in new[] { true, false })
            {
                var window = new PublishReviewWindow(Review(configMgr: !wsusOnly, wsus: wsusOnly));
                RenderKit.Show(window);
                try
                {
                    window.UpdateLayout();
                    var note = RenderKit.ById<TextBlock>(window, "txtReviewPlanNote").Text;
                    Assert.Equal(!wsusOnly, note.Contains("ConfigMgr is read again", StringComparison.Ordinal));
                    Assert.Equal(wsusOnly, note.Contains("does not change an update that WSUS already has", StringComparison.Ordinal));
                    Assert.Equal(!wsusOnly, ((FrameworkElement)window.FindName("txtReviewComment")).IsVisible);
                    var grid = RenderKit.ById<DataGrid>(window, "gridReviewMatrix");
                    Assert.Equal(["Application", "Version", "Checked", wsusOnly ? "WSUS" : "ConfigMgr"], grid.Columns.Select(c => (string)c.Header));
                }
                finally { window.Close(); }
            }
        });
    }
}
