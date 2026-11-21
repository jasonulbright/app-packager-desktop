using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-106: the registry of the Application Settings grid. Each setting is one registration; the grid, the column chooser and the bulk bar read
/// it. A setting that does not apply to an application says so with a reason, and a write to it changes nothing.
/// </summary>
public class ApplicationColumnsTests
{
    private static SupersedenceRow Row(bool arch = true, bool scope = true, bool network = true, string wsus = "") => new()
    {
        Uid = "u1", ApplicationId = "catalog:aaaaaaaaaaaaaaaa", DefinitionId = "def1", Application = "App", Vendor = "Vendor", Architecture = "x64",
        OffersArchitecture = arch, OffersInstallScope = scope, OffersNetworkSplit = network, WsusNotSupported = wsus
    };

    private static ApplicationWork Work(OneClickRow? oneClick = null) => new() { OneClickRowOf = _ => oneClick };

    private static OneClickRow OneClick() => new() { Uid = "u1", Application = "App", Vendor = "Vendor" };

    [Fact]
    public void EveryColumnHasAnIdAHeaderATooltipAndAResetValueThatItAccepts()
    {
        Assert.Equal(ApplicationColumns.All.Count, ApplicationColumns.All.Select(c => c.Id).Distinct().Count());
        var work = Work(OneClick());
        var row = Row();
        foreach (var column in ApplicationColumns.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(column.Header));
            Assert.False(string.IsNullOrWhiteSpace(column.Tooltip));
            Assert.False(string.IsNullOrWhiteSpace(column.AutomationName));
            Assert.True(column.Set(work, row, column.ResetValue), column.Id);
            Assert.Equal(column.ResetValue, column.Get(work, row) is { Length: > 0 } v ? v : column.ResetValue);
        }
    }

    [Fact]
    public void EveryColumnWithChoicesRefusesAWordThatIsNotAChoice()
    {
        var work = Work(OneClick());
        var row = Row();
        foreach (var column in ApplicationColumns.All.Where(c => c.Choices is not null && c.Id != "cadence"))
            Assert.False(column.Set(work, row, "Not a choice"), column.Id);
    }

    [Fact]
    public void ASettingThatDoesNotApplyGivesAReasonAndTheGridDoesNotWriteIt()
    {
        var plain = Row(arch: false, scope: false, network: false, wsus: "NoUserContext");
        var work = Work(OneClick());
        foreach (var id in new[] { "source", "scope", "split", "toWsus" })
        {
            var column = ApplicationColumns.Find(id)!;
            Assert.False(string.IsNullOrWhiteSpace(column.NotApplicable(work, plain)), id);
        }
        Assert.Null(ApplicationColumns.Find("rule")!.NotApplicable(work, plain));
        Assert.Contains("x64", ApplicationColumns.Find("source")!.NotApplicable(work, plain)!);
        Assert.Contains("NoUserContext", ApplicationColumns.Find("toWsus")!.NotApplicable(work, plain)!);
    }

    [Fact]
    public void TheOneClickColumnsNeedARowOfOneClick()
    {
        var row = Row();
        foreach (var id in new[] { "tracked", "cadence", "toConfigMgr", "toIntune", "toWsus" })
            Assert.NotNull(ApplicationColumns.Find(id)!.NotApplicable(Work(null), row));
        foreach (var id in new[] { "tracked", "cadence", "toConfigMgr", "toIntune" })
            Assert.Null(ApplicationColumns.Find(id)!.NotApplicable(Work(OneClick()), row));
    }

    [Fact]
    public void TheNetworkColumnsWriteTheRequirementAndTheSplitOfTheStoredConditions()
    {
        var work = Work();
        var row = Row();
        Assert.True(ApplicationColumns.Find("network")!.Set(work, row, "VPN only"));
        Assert.Equal("VpnOnly", work.Conditions["def1"].Network);
        Assert.Single(DeploymentConditionsService.BuildRequirements(work.Conditions["def1"]));
        Assert.True(ApplicationColumns.Find("split")!.Set(work, row, "VPN and managed"));
        Assert.Equal("Network", work.Conditions["def1"].Split);
        Assert.Contains("def1", work.ConditionsTouched);
        ApplicationColumns.Find("split")!.Set(work, row, "No split");
        ApplicationColumns.Find("network")!.Set(work, row, "Any network");
        Assert.Empty(work.Conditions);
    }

    [Fact]
    public void TheCommandColumnsWriteTheOverridesAndAnEmptyPairIsRemoved()
    {
        var work = Work();
        var row = Row();
        ApplicationColumns.Find("install")!.Set(work, row, "  setup.exe /S  ");
        Assert.Equal("setup.exe /S", work.Commands["def1"].Install);
        ApplicationColumns.Find("uninstall")!.Set(work, row, "uninst.exe /S");
        Assert.Equal("setup.exe /S", work.Commands["def1"].Install);
        ApplicationColumns.Find("install")!.Set(work, row, "");
        ApplicationColumns.Find("uninstall")!.Set(work, row, "");
        Assert.Empty(work.Commands);
        Assert.Contains("def1", work.CommandsTouched);
    }

    [Fact]
    public void TheOneClickColumnsWriteTheRowThatOneClickSaves()
    {
        var oc = OneClick();
        oc.ConfigMgr = true;
        var work = Work(oc);
        work.DefaultDestinations = () => new DestinationSet(true, false, false);
        var row = Row();
        ApplicationColumns.Find("tracked")!.Set(work, row, "Yes");
        Assert.True(oc.Tracked);
        Assert.True(ApplicationColumns.Find("cadence")!.Set(work, row, "14"));
        Assert.Equal(14, oc.CadenceOverride);
        Assert.False(ApplicationColumns.Find("cadence")!.Set(work, row, "soon"));
        ApplicationColumns.Find("toIntune")!.Set(work, row, "Yes");
        Assert.True(oc.Intune);
        Assert.Equal("Yes", ApplicationColumns.Find("toIntune")!.Get(work, row));
        ApplicationColumns.Find("toIntune")!.Set(work, row, "Follow the default");
        Assert.False(oc.Intune);
        Assert.Equal("Follow the default", ApplicationColumns.Find("toIntune")!.Get(work, row));
    }

    [Fact]
    public void TheTitleCellIsLockedWhileSupersedenceIsOnAndTheReasonSaysWhy()
    {
        var work = Work();
        var row = Row();
        var column = ApplicationColumns.Find("title")!;
        Assert.Null(column.NotApplicable(work, row));
        ApplicationColumns.Find("supersedence")!.Set(work, row, "Replace");
        Assert.Contains("Supersedence is on", column.NotApplicable(work, row)!);
        Assert.Equal("Include version", column.Get(work, row));
        Assert.False(column.Set(work, row, "No version"));
    }
}
