using System.Text;
using System.Windows.Media;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-50: the older-application override ("Use as old application"), ported from the PowerShell app (Tests/SupersedenceOldApplication.Tests.ps1,
/// the same cases). Only an application without the marker of another line and without keywords of its own is offered, the title and the version
/// are confirmed first, the marker is written only after the confirmation, and the lookup then finds the linked application like any other of the
/// line. The site is answered by a fake script runner.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SupersedenceOldApplicationTests
{
    private const string Marker = "APD-Line:catalog:vendor-app|default|x64";

    private static SiteApplicationRecord Site(string title, string version, bool expired = false, string[]? keywords = null, bool unreadable = false) =>
        new(title, version, expired, unreadable ? null : keywords ?? []);

    private static IReadOnlyList<OldApplicationCandidate> Candidates(
        IReadOnlyList<SiteApplicationRecord> apps, string newTitle = "Vendor App 2.0", string newVersion = "2.0.0") =>
        OldApplicationRules.Classify(apps, Marker, newTitle, newVersion);

    private static OldApplicationCandidate One(SiteApplicationRecord app, string newVersion = "2.0.0") => Assert.Single(Candidates([app], newVersion: newVersion));

    // ---- the list of applications that can be linked

    [Fact]
    public void OffersAnApplicationWithoutTheMarkerAndWithoutKeywords()
    {
        var row = One(Site("Vendor App 1.0", "1.0.0"));
        Assert.Equal(OldApplicationState.Linkable, row.State);
        Assert.True(row.CanLink);
    }

    [Fact]
    public void ListsTheLinkableApplicationsFirstTheNewestVersionFirst()
    {
        var rows = Candidates([Site("Vendor App retired", "1.9.0", expired: true), Site("Vendor App 1.0", "1.0.0"), Site("Vendor App 1.5", "1.5.0")]);
        Assert.Equal(["Vendor App 1.5", "Vendor App 1.0", "Vendor App retired"], rows.Select(r => r.Title));
    }

    [Fact]
    public void NeverListsAnApplicationWithTheTitleOfTheNewOne() => Assert.Empty(Candidates([Site("vendor app 2.0", "1.0.0")]));

    [Fact]
    public void MarksARetiredApplicationAndDoesNotOfferIt()
    {
        var row = One(Site("Vendor App 1.0", "1.0.0", expired: true));
        Assert.Equal(OldApplicationState.Retired, row.State);
        Assert.False(row.CanLink);
    }

    [Fact]
    public void DoesNotOfferAVersionThatCannotBeReadAndSaysWhy()
    {
        var row = One(Site("Vendor App old", "banana"));
        Assert.Equal(OldApplicationState.VersionUnreadable, row.State);
        Assert.Contains("banana", row.Reason);
        Assert.False(row.CanLink);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("3.0.0")]
    public void DoesNotOfferAVersionThatIsNotOlderBecauseTheLookupWouldNotChooseIt(string version)
    {
        var row = One(Site("Vendor App 3", version));
        Assert.Equal(OldApplicationState.NotOlder, row.State);
        Assert.False(row.CanLink);
    }

    [Fact]
    public void DoesNotOfferAnApplicationThatHasKeywordsOfItsOwn()
    {
        var row = One(Site("Vendor App 1.0", "1.0.0", keywords: ["finance", "vendor"]));
        Assert.Equal(OldApplicationState.OwnKeywords, row.State);
        Assert.False(row.CanLink);
        Assert.False(row.CanClear);
    }

    [Fact]
    public void DoesNotOfferAnApplicationThatHoldsTheMarkerOfAnotherLine()
    {
        var row = One(Site("Vendor App 1.0", "1.0.0", keywords: ["APD-Line:catalog:other-app|default|x64"]));
        Assert.Equal(OldApplicationState.OtherLine, row.State);
        Assert.False(row.CanLink);
    }

    [Fact]
    public void DoesNotOfferTheMarkerOfAnotherProfileOrArchitectureOfTheSameTitle()
    {
        Assert.Equal(OldApplicationState.OtherLine, One(Site("A", "1.0.0", keywords: ["APD-Line:catalog:vendor-app|pilot|x64"])).State);
        Assert.Equal(OldApplicationState.OtherLine, One(Site("B", "1.0.0", keywords: ["APD-Line:catalog:vendor-app|default|x86"])).State);
    }

    [Fact]
    public void ShowsAnApplicationThatAlreadyHoldsTheMarkerAsLinkedAndItCanBeCleared()
    {
        var row = One(Site("Vendor App 1.0", "1.0.0", keywords: [Marker]));
        Assert.Equal(OldApplicationState.Linked, row.State);
        Assert.False(row.CanLink);
        Assert.True(row.CanClear);
    }

    [Fact]
    public void CannotClearAMarkerThatSitsNextToKeywordsOfAnAdministrator()
    {
        var row = One(Site("Vendor App 1.0", "1.0.0", keywords: [Marker, "finance"]));
        Assert.Equal(OldApplicationState.Linked, row.State);
        Assert.False(row.CanClear);
    }

    [Fact]
    public void DoesNotOfferAnApplicationWhoseKeywordsCouldNotBeRead()
    {
        var row = One(Site("Vendor App 1.0", "1.0.0", unreadable: true));
        Assert.Equal(OldApplicationState.KeywordsUnreadable, row.State);
        Assert.False(row.CanLink);
    }

    [Fact]
    public void GivesATitleWithoutAReadableVersionNoOldApplication() =>
        Assert.False(One(Site("Vendor App 1.0", "1.0.0"), newVersion: "banana").CanLink);

    [Fact]
    public void GivesATitleWithNoApplicationsAnEmptyList() => Assert.Empty(Candidates([]));

    // ---- the lookup honors a linked application

    [Fact]
    public void TheLookupDoesNotChooseAnApplicationWithoutTheMarkerAndChoosesItOnceTheMarkerIsWritten()
    {
        // The lookup reads the site by the marker, so an application without it is not in the list that the lookup gets.
        Assert.Null(SupersedenceLookup.Choose([], "Vendor App 2.0", "2.0.0").Old);
        var linked = new LineApplication("1", "Vendor App 1.0", "1.0.0", false, []);
        Assert.Equal("1", SupersedenceLookup.Choose([linked], "Vendor App 2.0", "2.0.0").Old!.CiId);
    }

    [Fact]
    public void TheLookupFindsAnApplicationByTheMarkerThatTheLinkWroteIntoTheStoredXml()
    {
        // The link writes the marker as the keyword; the lookup script matches ">marker<" in the stored XML, which holds the keyword as a Tag.
        var link = OldApplicationScripts.BuildLinkScript("P01", null, "Vendor App 1.0", Marker);
        Assert.Contains($"-Keyword '{Marker}'", link);
        var lookup = SupersedenceScripts.BuildLookupScript("P01", null, Marker, "Vendor App 2.0");
        Assert.Contains($">{Marker}<", lookup);
        var output = $"{SupersedenceLookup.ApplicationLine}|1|0|1.0.0|{Convert.ToBase64String(Encoding.UTF8.GetBytes("Vendor App 1.0"))}\n";
        var chosen = SupersedenceLookup.Choose(SupersedenceLookup.ParseSiteApplications(output), "Vendor App 2.0", "2.0.0");
        Assert.Equal("Vendor App 1.0", chosen.Old!.Title);
    }

    // ---- the read of the site for the list

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string SearchLine(string title, string version, string state = "ok", string keywords = "", bool expired = false) =>
        $"{OldApplicationScripts.ApplicationLine}|{(expired ? 1 : 0)}|{B64(version)}|{B64(title)}|{state}|{B64(keywords)}\n";

    private sealed class FakeSite(Func<string, (int, string, string)> answer)
    {
        public List<string> Scripts { get; } = [];
        public SiteScriptRunner Runner => (script, _) => { Scripts.Add(script); return Task.FromResult(answer(script)); };
        public int Writes => Scripts.Count(s => s.Contains("Set-CMApplication", StringComparison.Ordinal));
    }

    private static OldApplicationService Service(FakeSite site) => new("P01", null, Marker, site.Runner);

    [Fact]
    public async Task TheSearchAsksForANameThatContainsTheTextAndTreatsTheWildcardCharactersAsLiteral()
    {
        var site = new FakeSite(_ => (0, SearchLine("Vendor App 1.0", "1.0.0") + SearchLine("Vendor App 1.5", "1.5.0"), ""));
        var rows = await Service(site).SearchAsync("Vendor App 2.0", "2.0.0", "App [1]*", CancellationToken.None);

        var script = Assert.Single(site.Scripts);
        Assert.Contains("[System.Management.Automation.WildcardPattern]::Escape('App [1]*')", script);
        Assert.Contains("'*' +", script);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task ListsAnApplicationWhoseKeywordsCannotBeReadAsNotLinkable()
    {
        var site = new FakeSite(_ => (0, SearchLine("Vendor App 1.0", "1.0.0") + SearchLine("Vendor App 1.5", "1.5.0", state: "unreadable"), ""));
        var rows = await Service(site).SearchAsync("Vendor App 2.0", "2.0.0", "Vendor", CancellationToken.None);

        Assert.Equal(OldApplicationState.KeywordsUnreadable, rows.Single(r => r.Title == "Vendor App 1.5").State);
        Assert.Equal(OldApplicationState.Linkable, rows.Single(r => r.Title == "Vendor App 1.0").State);
    }

    [Fact]
    public async Task ReadsKeywordsAndTheRetiredFlagFromTheSiteOutput()
    {
        var site = new FakeSite(_ => (0, SearchLine("Vendor App 1.0", "1.0.0", keywords: Marker, expired: true) + SearchLine("Vendor App 0.9", "0.9.0", keywords: "finance\nvendor"), ""));
        var rows = await Service(site).SearchAsync("Vendor App 2.0", "2.0.0", "Vendor", CancellationToken.None);

        Assert.Equal(OldApplicationState.Linked, rows.Single(r => r.Title == "Vendor App 1.0").State);
        Assert.Equal(OldApplicationState.OwnKeywords, rows.Single(r => r.Title == "Vendor App 0.9").State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RefusesAnEmptySearchTextAndReadsNothing(string text)
    {
        var site = new FakeSite(_ => (0, "", ""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(site).SearchAsync("T", "2.0.0", text, CancellationToken.None));

        Assert.Contains("part of the application name", ex.Message);
        Assert.Empty(site.Scripts);
    }

    [Fact]
    public async Task ReportsASiteThatCouldNotBeRead()
    {
        var site = new FakeSite(_ => (1, "", "no provider"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(site).SearchAsync("T", "2.0.0", "Vendor", CancellationToken.None));
        Assert.Contains("no provider", ex.Message);
    }

    // ---- the confirmation before the marker is written

    [Fact]
    public void TheConfirmationShowsTheTitleAndTheVersionFirstNamesTheNewApplicationAndSaysWhatIsNotChanged()
    {
        var text = OldApplicationRules.LinkConfirmationText("Vendor App 1.0", "1.0.0", "Vendor App 2.0");

        Assert.StartsWith("Use 'Vendor App 1.0' version 1.0.0 as the old application of 'Vendor App 2.0'?", text);
        Assert.Contains("identity marker", text);
        Assert.Contains("deployment types, detection and deployments are not changed", text);
    }

    [Fact]
    public void TheRemovalSaysThatTheNextPublishNoLongerFindsTheApplication() =>
        Assert.Contains("no longer finds it", OldApplicationRules.ClearConfirmationText("Vendor App 1.0", "1.0.0"));

    [Fact]
    public async Task WritesNothingWithoutTheConfirmation()
    {
        var site = new FakeSite(_ => (0, $"{OldApplicationScripts.KeywordLine}|{B64("")}\n", ""));
        var result = await Service(site).LinkAsync("Vendor App 1.0", confirmed: false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(site.Scripts);
    }

    [Fact]
    public async Task RemovesNothingWithoutTheConfirmation()
    {
        var site = new FakeSite(_ => (0, "", ""));
        var result = await Service(site).ClearAsync("Vendor App 1.0", confirmed: false, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(site.Scripts);
    }

    // ---- linking an application

    private static FakeSite SiteWithKeywords(string keywords, int writeExit = 0, string writeError = "") =>
        new(script => script.Contains("Set-CMApplication", StringComparison.Ordinal)
            ? (writeExit, "", writeError)
            : (0, $"{OldApplicationScripts.KeywordLine}|{B64(keywords)}\n", ""));

    [Fact]
    public async Task WritesTheMarkerOfTheLineByTheNameOfTheApplicationAfterTheConfirmation()
    {
        var site = SiteWithKeywords("");
        var result = await Service(site).LinkAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, site.Writes);
        var write = site.Scripts.Single(s => s.Contains("Set-CMApplication", StringComparison.Ordinal));
        Assert.Contains("Get-CMApplication -Name 'Vendor App 1.0' -DisableWildcardHandling", write);
        Assert.Contains($"-Keyword '{Marker}'", write);
    }

    [Fact]
    public async Task RefusesAnApplicationThatHasKeywordsOfItsOwnAndWritesNothing()
    {
        var site = SiteWithKeywords("finance");
        var result = await Service(site).LinkAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("keywords of its own", result.Message);
        Assert.Equal(0, site.Writes);
    }

    [Fact]
    public async Task RefusesAnApplicationThatHoldsTheMarkerOfAnotherLineAndWritesNothing()
    {
        var site = SiteWithKeywords("APD-Line:catalog:other-app|default|x64");
        var result = await Service(site).LinkAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("another line", result.Message);
        Assert.Equal(0, site.Writes);
    }

    [Fact]
    public async Task DoesNotWriteAgainWhenTheApplicationAlreadyHoldsTheMarker()
    {
        var site = SiteWithKeywords(Marker);
        var result = await Service(site).LinkAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, site.Writes);
    }

    [Fact]
    public async Task ReportsTheReasonWhenTheKeywordsCannotBeReadAndWritesNothing()
    {
        var site = new FakeSite(_ => (1, "", "the digest is damaged"));
        var result = await Service(site).LinkAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("could not be read", result.Message);
        Assert.Equal(0, site.Writes);
    }

    [Fact]
    public async Task ReportsAFailedWriteWithItsReason()
    {
        var site = SiteWithKeywords("", writeExit: 1, writeError: "access denied");
        var result = await Service(site).LinkAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("access denied", result.Message);
    }

    [Fact]
    public async Task ReportsAnApplicationThatIsNotOnTheSite()
    {
        var site = new FakeSite(_ => (0, OldApplicationScripts.MissingLine + "\n", ""));
        var result = await Service(site).LinkAsync("Gone", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not on the site", result.Message);
    }

    [Fact]
    public async Task RefusesATitleThatTwoApplicationsHold()
    {
        var site = new FakeSite(_ => (0, OldApplicationScripts.AmbiguousLine + "\n", ""));
        var result = await Service(site).LinkAsync("Twin", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, site.Writes);
    }

    [Fact]
    public void TheLinkScriptReadsTheKeywordsAgainAndWritesOnlyWhenThereAreNone()
    {
        var script = OldApplicationScripts.BuildLinkScript("P01", null, "Vendor 'App'", Marker);

        Assert.Contains("Get-CMApplication -Name 'Vendor ''App'''", script);
        Assert.True(script.IndexOf("$tags.Count -gt 0", StringComparison.Ordinal) < script.IndexOf("Set-CMApplication", StringComparison.Ordinal));
    }

    // ---- removing a link

    [Fact]
    public async Task EmptiesTheKeywordsOfAnApplicationWhoseOnlyKeywordIsTheMarkerOfTheLine()
    {
        var site = SiteWithKeywords(Marker);
        var result = await Service(site).ClearAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, site.Writes);
        Assert.Contains("-Keyword ''", site.Scripts.Single(s => s.Contains("Set-CMApplication", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DoesNotTouchAnApplicationThatHoldsKeywordsOfAnAdministratorNextToTheMarker()
    {
        var site = SiteWithKeywords(Marker + "\nfinance");
        var result = await Service(site).ClearAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, site.Writes);
    }

    [Fact]
    public async Task DoesNotTouchAnApplicationThatHoldsTheMarkerOfAnotherLine()
    {
        var site = SiteWithKeywords("APD-Line:catalog:other-app|default|x64");
        var result = await Service(site).ClearAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, site.Writes);
    }

    [Fact]
    public async Task ReportsAFailedRemoval()
    {
        var site = SiteWithKeywords(Marker, writeExit: 1, writeError: "access denied");
        var result = await Service(site).ClearAsync("Vendor App 1.0", confirmed: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("access denied", result.Message);
    }

    // ---- the grid menu and the window

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([CatalogServiceTests.RepositoryRoot(), .. parts]));

    [Fact]
    public void TheGridMenuHasTheItemAndOpensTheWindowForTheSelectedRow()
    {
        var xaml = Source("src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.xaml");
        var code = Source("src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.xaml.cs");

        Assert.Contains("Header=\"Use as old application...\"", xaml);
        Assert.Contains("Click=\"ContextMenu_UseAsOldApplication\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"menuUseAsOldApplication\"", xaml);
        Assert.Contains("GetSelectedPackager() is not { } row || Packages.OldApplicationTargetFor(row)", code);
        Assert.Contains("new Views.Windows.OldApplicationWindow(viewModel)", code);
    }

    [Fact]
    public void TheMarkerAndTheTitleComeFromTheStagedBuildAsThePublishResolvesThem()
    {
        var service = Source("src", "ApplicationPackagerUI", "Services", "PackagerExecutionService.cs");
        var method = service[service.IndexOf("OldApplicationTargetFor(IPackagerExecutionRequest", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("What the \"Apply supersedence\" action did", StringComparison.Ordinal)];

        Assert.Contains("ExistingStageManifest(row)", method);
        Assert.Contains("No stage manifest was found", method);
        Assert.Contains("IdentityMarker.For(manifest, fallbackApplicationId)", method);
        Assert.Contains("RunApplicationTitle(manifest, titleMode, settings)", method);
    }

    [Fact]
    public void TheWindowAsksBeforeItWritesAndCancelIsTheDefaultOfTheConfirmation()
    {
        var model = Source("src", "ApplicationPackagerUI", "ViewModels", "Windows", "OldApplicationViewModel.cs");
        var window = Source("src", "ApplicationPackagerUI", "Views", "Windows", "OldApplicationWindow.xaml.cs");

        Assert.True(model.IndexOf("LinkConfirmationText", StringComparison.Ordinal) < model.IndexOf("LinkAsync(row.Title, confirmed: true", StringComparison.Ordinal));
        Assert.True(model.IndexOf("ClearConfirmationText", StringComparison.Ordinal) < model.IndexOf("ClearAsync(row.Title, confirmed: true", StringComparison.Ordinal));
        Assert.Contains("DefaultButton = ContentDialogButton.Close", window);
    }

    [Fact]
    public void TheSettingsImportNeedsNothingBecauseTheLinkLivesOnTheSite()
    {
        // The link is the marker in the keywords of the application on the site; no preference holds it, so the PowerShell import has no key to map.
        var import = Source("src", "ApplicationPackagerUI", "Services", "PowerShellOptionsImport.cs");
        Assert.DoesNotContain("OldApplication", import, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the window, rendered

    public static TheoryData<ApplicationTheme> Themes() => new() { ApplicationTheme.Light, ApplicationTheme.Dark };

    private static FakeSite WindowSite(List<string> confirmations) => new(script =>
    {
        if (script.Contains("APDOLD", StringComparison.Ordinal))
            return (0, SearchLine("Vendor App 1.5", "1.5.0") + SearchLine("Vendor App 1.0", "1.0.0", keywords: Marker)
                + SearchLine("Vendor App 0.9", "0.9.0", keywords: "finance") + SearchLine("Vendor App Other", "1.2.0", keywords: "APD-Line:catalog:other-app|default|x64")
                + SearchLine("Vendor App 3.0 beta", "3.0.0") + SearchLine("Vendor App old", "banana"), "");
        if (script.Contains("Set-CMApplication", StringComparison.Ordinal)) return (0, "", "");
        return (0, $"{OldApplicationScripts.KeywordLine}|{B64("")}\n", "");
    });

    private static OldApplicationViewModel Model(FakeSite site, List<string> confirmations, bool answer = true) =>
        new(Service(site), "Vendor App 2.0", "2.0.0", (title, message) => { confirmations.Add(title + "|" + message); return Task.FromResult(answer); });

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheWindowListsTheApplicationsWithTheirStateAndEnablesTheButtonsByTheSelectedRow(ApplicationTheme theme) => WithApplication(theme, () =>
    {
        var confirmations = new List<string>();
        var site = WindowSite(confirmations);
        var model = Model(site, confirmations);
        var window = new OldApplicationWindow(model) { Width = 1920, Height = 1032, Left = -32000, Top = -32000, ShowInTaskbar = false };
        Show(window);
        try
        {
            Settle(window);
            var grid = ById<System.Windows.Controls.DataGrid>(window, "dgOldApplications");
            var link = ById<Wpf.Ui.Controls.Button>(window, "btnOldApplicationLink");
            var clear = ById<Wpf.Ui.Controls.Button>(window, "btnOldApplicationClear");
            Assert.False(ById<Wpf.Ui.Controls.Button>(window, "btnOldApplicationSearch").IsEnabled);
            Assert.False(link.IsEnabled);
            Assert.True(ById<Wpf.Ui.Controls.Button>(window, "btnOldApplicationClose").IsCancel);

            model.SearchText = "Vendor App";
            Run(model.SearchCommand.ExecuteAsync(null));
            Settle(window);

            var rows = grid.Items.Cast<OldApplicationRow>().ToList();
            Assert.Equal(6, rows.Count);
            Assert.Equal("Vendor App 1.5", rows[0].Title);
            Assert.Equal("Can be linked", rows[0].StateText);
            Assert.Contains("1 can be linked", ById<System.Windows.Controls.TextBlock>(window, "txtOldApplicationStatus").Text);

            grid.SelectedItem = rows[0];
            Settle(window);
            Assert.True(link.IsEnabled);
            Assert.False(clear.IsEnabled);

            grid.SelectedItem = rows.Single(r => r.Candidate.State == OldApplicationState.Linked);
            Settle(window);
            Assert.False(link.IsEnabled);
            Assert.True(clear.IsEnabled);

            grid.SelectedItem = rows.Single(r => r.Candidate.State == OldApplicationState.OwnKeywords);
            Settle(window);
            Assert.False(link.IsEnabled);
            Assert.False(clear.IsEnabled);

            grid.SelectedItem = rows[0];
            Settle(window);
            Shot(window, $"old-application-{theme.ToString().ToLowerInvariant()}.png");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LinkingAsksFirstWithTitleAndVersionThenWritesTheMarkerAndReadsTheSiteAgain() => WithApplication(ApplicationTheme.Light, () =>
    {
        var confirmations = new List<string>();
        var site = WindowSite(confirmations);
        var model = Model(site, confirmations);
        model.SearchText = "Vendor App";
        Run(model.SearchCommand.ExecuteAsync(null));
        model.Selected = model.Rows[0];

        Run(model.LinkCommand.ExecuteAsync(null));

        var asked = Assert.Single(confirmations);
        Assert.StartsWith("Use as Old Application|Use 'Vendor App 1.5' version 1.5.0 as the old application of 'Vendor App 2.0'?", asked);
        Assert.Equal(1, site.Writes);
        Assert.Equal(2, site.Scripts.Count(s => s.Contains("APDOLD", StringComparison.Ordinal)));
        Assert.Contains("is the old application of this line", model.StatusText);
    });

    [Fact]
    public void ADeclinedConfirmationWritesNothing() => WithApplication(ApplicationTheme.Light, () =>
    {
        var confirmations = new List<string>();
        var site = WindowSite(confirmations);
        var model = Model(site, confirmations, answer: false);
        model.SearchText = "Vendor App";
        Run(model.SearchCommand.ExecuteAsync(null));
        model.Selected = model.Rows[0];

        Run(model.LinkCommand.ExecuteAsync(null));

        Assert.Single(confirmations);
        Assert.Equal(0, site.Writes);
        Assert.Contains("Nothing was changed", model.StatusText);
    });

    [Fact]
    public void RemoveLinkAsksFirstAndClearsOnlyTheMarkerAlone() => WithApplication(ApplicationTheme.Light, () =>
    {
        var confirmations = new List<string>();
        var site = new FakeSite(script =>
        {
            if (script.Contains("APDOLD", StringComparison.Ordinal)) return (0, SearchLine("Vendor App 1.0", "1.0.0", keywords: Marker), "");
            if (script.Contains("Set-CMApplication", StringComparison.Ordinal)) return (0, "", "");
            return (0, $"{OldApplicationScripts.KeywordLine}|{B64(Marker)}\n", "");
        });
        var model = Model(site, confirmations);
        model.SearchText = "Vendor";
        Run(model.SearchCommand.ExecuteAsync(null));
        model.Selected = model.Rows[0];

        Run(model.ClearCommand.ExecuteAsync(null));

        Assert.StartsWith("Remove Link|Remove the identity marker from 'Vendor App 1.0' version 1.0.0?", Assert.Single(confirmations));
        Assert.Equal(1, site.Writes);
        Assert.Contains("-Keyword ''", site.Scripts.Single(s => s.Contains("Set-CMApplication", StringComparison.Ordinal)));
    });

    private static void Shot(System.Windows.Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("APD_SETTINGS_SHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }
}
