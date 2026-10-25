using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygienePageHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The page binds stored scans, fixes and suppressions to the site and SMS Provider, and shows a fix whose audit record is missing.</summary>
public sealed class SiteHygienePageConnectionTests
{
    [Fact]
    public async Task AStoredScanOfAnotherProviderWithTheSameSiteCodeIsNotShownAndIsNoBaseline()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result() with { Stored = true, Provider = "cm02.contoso.com" };
        var vm = await h.OpenAsync();
        Assert.False(vm.HasResult);
        Assert.Equal("The stored last scan is of site MCM on cm02.contoso.com, not of site MCM on cm01.contoso.com, so it is not shown. Select Scan to read site MCM.", vm.StatusMessage);

        h.Service.Findings.Clear();
        h.Service.Findings.Add(h.UnusedApp);
        await vm.ScanAsync();
        Assert.False(Assert.Single(vm.FindingRows).Finding.IsNew);
    }

    [Fact]
    public async Task AStoredScanWithoutARecordedProviderIsNotShown()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result() with { Stored = true, Provider = null };
        var vm = await h.OpenAsync();
        Assert.False(vm.HasResult);
        Assert.Contains("(not known)", vm.StatusMessage);
    }

    [Fact]
    public async Task AFixIsRefusedWhenTheSettingsNameAnotherProviderThanTheResult()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Settings = new AppSettings { SiteCode = "MCM", ProviderMachine = "cm02.contoso.com" };
        Select(vm, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(0, h.Service.PrepareCalls);
        Assert.Empty(h.Plans.Shown);
        Assert.Equal(VM.OtherConnection(vm.Result!, "MCM", "cm02.contoso.com"), vm.StatusMessage);
        Assert.Contains("Nothing was changed.", vm.StatusMessage);
    }

    [Fact]
    public async Task ASuppressionRecordsTheProviderAndASuppressionOfAnotherProviderHidesNothing()
    {
        var h = new SiteHygienePageHarness();
        h.Suppressions.Items.Add(new Suppression("DEV-02", "16777221", "Duplicate on the other hierarchy.", DateTime.UtcNow) { SiteCode = "MCM", Provider = "CM02.CONTOSO.COM" });
        h.Suppressions.Items.Add(new Suppression("BND-01", "16777217", "Recorded before the provider was kept.", DateTime.UtcNow) { SiteCode = "MCM" });
        var vm = await h.ScannedAsync();
        Assert.Contains(vm.FindingRows, r => r.Key == SiteHygieneLabels.Key(h.Duplicate));
        Assert.DoesNotContain(vm.FindingRows, r => r.Key == SiteHygieneLabels.Key(h.Boundary));

        Select(vm, h.UnusedApp);
        await vm.SuppressCommand.ExecuteAsync(null);
        var added = h.Suppressions.Items.Single(s => s.CheckId == "APP-01");
        Assert.Equal(("MCM", "cm01.contoso.com"), (added.SiteCode, added.Provider));
    }

    [Fact]
    public async Task AFixWhoseFinishedAuditRecordIsMissingShowsAWarningOnThePageAndInThePlan()
    {
        var h = new SiteHygienePageHarness();
        h.Service.AuditIncomplete = true;
        var vm = await h.ScannedAsync();
        Select(vm, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.True(vm.HasAuditWarning);
        Assert.Equal(VM.AuditWarningText(@"C:\Data\SiteHygiene\site-hygiene-audit.jsonl"), vm.AuditWarning);
        Assert.Equal(SiteHygieneWriter.AuditIncompleteNote, h.Plans.LastOutcome!.Result!.Note);
        Assert.Equal("Done", Assert.Single(vm.LastResults).OutcomeText);

        h.Service.AuditIncomplete = false;
        Select(vm, h.RefreshType);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.False(vm.HasAuditWarning);
    }
}
