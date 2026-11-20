using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygienePageHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Show blockers action of the Site Hygiene page: one application, the data of the last scan, no site call.</summary>
public sealed class SiteHygieneBlockersPageTests
{
    private const string Model = "ScopeId_X/Application_Old";

    private static HygieneFinding AppFinding(SiteHygienePageHarness h) => h.Service.Findings.AddAndReturn(
        Finding("APP-08", "Applications", HygieneSeverity.Info, "Application", Model, "Contoso Old 1.0", HygieneFixKind.RemoveSupersededApplication, "Remove-CMApplication -Id 1 -Force"));

    [Fact]
    public async Task TheWindowListsWhatBlocksTheDeleteOfTheSelectedApplicationAndReadsNothingFromTheSite()
    {
        var h = new SiteHygienePageHarness();
        var finding = AppFinding(h);
        h.Service.Blockers = new() { [Model] = [new(HygieneBlockerKinds.Deployment, "All Desktops", "Collection All Desktops (MCM00001)"), new(HygieneBlockerKinds.TaskSequence, "Build PC", "Package MCM00010")] };
        var vm = await h.ScannedAsync();
        var calls = (h.Service.ScanCalls, h.Service.PrepareCalls, h.Service.RelationCalls);
        Select(vm, finding);
        vm.ShowBlockersCommand.Execute(null);
        var shown = Assert.Single(h.Dialogs.BlockersShown);
        Assert.Equal("Contoso Old 1.0", shown.ApplicationName);
        Assert.Equal(Model, shown.ModelName);
        Assert.Equal(2, shown.Rows.Count);
        Assert.Contains("2 objects refer to the application", shown.Summary);
        Assert.True(shown.HasNote);
        Assert.Equal(calls, (h.Service.ScanCalls, h.Service.PrepareCalls, h.Service.RelationCalls));
        Assert.Equal("", vm.StatusMessage);
    }

    [Fact]
    public async Task AnApplicationWithoutAnEntryHasNoBlockerAndAScanWithoutTheDataAsksForANewScan()
    {
        var h = new SiteHygienePageHarness();
        var finding = AppFinding(h);
        h.Service.Blockers = [];
        var vm = await h.ScannedAsync();
        Select(vm, finding);
        vm.ShowBlockersCommand.Execute(null);
        var none = Assert.Single(h.Dialogs.BlockersShown);
        Assert.Empty(none.Rows);
        Assert.Equal(HygieneBlockersViewModel.NoBlocker, none.Summary);

        var h2 = new SiteHygienePageHarness();
        var finding2 = AppFinding(h2);
        h2.Service.Blockers = null;
        var vm2 = await h2.ScannedAsync();
        Select(vm2, finding2);
        vm2.ShowBlockersCommand.Execute(null);
        var unread = Assert.Single(h2.Dialogs.BlockersShown);
        Assert.False(unread.IsRead);
        Assert.Equal(HygieneBlockersViewModel.NotRead, unread.Summary);
    }

    [Fact]
    public async Task TheActionExplainsItselfWithoutAScanWithoutASelectionWithTwoFindingsAndWithAFindingThatIsNotAboutAnApplication()
    {
        var h = new SiteHygienePageHarness();
        var finding = AppFinding(h);
        var vm = await h.OpenAsync();
        vm.ShowBlockersCommand.Execute(null);
        Assert.Equal(VM.NotScanned, vm.StatusMessage);
        await vm.ScanAsync();
        vm.SetSelectedFindings([]);
        vm.ShowBlockersCommand.Execute(null);
        Assert.Equal(VM.BlockersSelectOne, vm.StatusMessage);
        Select(vm, finding, h.UnusedApp);
        vm.ShowBlockersCommand.Execute(null);
        Assert.Equal(VM.BlockersOneOnly, vm.StatusMessage);
        Select(vm, h.EmptyCollection);
        vm.ShowBlockersCommand.Execute(null);
        Assert.Equal(VM.BlockersNotAnApplication, vm.StatusMessage);
        Assert.Empty(h.Dialogs.BlockersShown);
    }

    [Fact]
    public void ATaskSequenceFindingNamesTheApplicationAfterThePackageId()
    {
        var f = Finding("TSQ-05", "Task Sequences", HygieneSeverity.Warning, "TaskSequence", "MCM00010|" + Model, "Build PC (MCM00010) -> Contoso Old 1.0", HygieneFixKind.None);
        Assert.Equal((Model, "Contoso Old 1.0"), VM.ApplicationOf(f));
        Assert.Null(VM.ApplicationOf(Finding("COL-01", "Collections", HygieneSeverity.Info, "Collection", "MCM00012", "Empty", HygieneFixKind.None)));
    }
}

internal static class BlockerTestLists
{
    public static T AddAndReturn<T>(this List<T> list, T item)
    {
        list.Add(item);
        return item;
    }
}
