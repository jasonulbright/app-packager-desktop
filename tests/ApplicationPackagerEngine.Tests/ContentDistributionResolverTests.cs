using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-106: the package run reads the content distribution of the application first and the site-wide value as the fallback. The columns
/// Distribution point group and Test collection of the grid write the per-application value.
/// </summary>
public class ContentDistributionResolverTests
{
    private const string App = "catalog:aaaaaaaaaaaaaaaa";

    private static AppSettings Site() => new()
    {
        ContentDistribution = new ContentDistributionSettings
        {
            AutoDistribute = true, DpGroupName = "Site DPs", DeployToTestCollection = true, TestCollectionName = "Site pilot", TestCollectionIsUser = true, CreateTestCollectionIfMissing = true
        }
    };

    [Fact]
    public void AnApplicationWithoutAValueFollowsTheSiteWideSettings()
    {
        var run = ContentDistributionResolver.For(Site(), App);
        Assert.True(run.AutoDistribute);
        Assert.Equal("Site DPs", run.DpGroupName);
        Assert.Equal("Site pilot", run.TestCollectionName);
        Assert.True(run.TestCollectionIsUser);
    }

    [Fact]
    public void AValueOfTheApplicationIsUsedFirstAndAnEmptyValueFallsBackToTheSiteWideOne()
    {
        var settings = Site();
        settings.DistributionOverrides[App] = new ApplicationDistribution { DpGroupName = "Pilot DPs", TestCollectionName = "" };
        var run = ContentDistributionResolver.For(settings, App);
        Assert.Equal("Pilot DPs", run.DpGroupName);
        Assert.Equal("Site pilot", run.TestCollectionName);

        settings.DistributionOverrides[App] = new ApplicationDistribution { DpGroupName = "", TestCollectionName = "Own pilot" };
        run = ContentDistributionResolver.For(settings, App);
        Assert.Equal("Site DPs", run.DpGroupName);
        Assert.Equal("Own pilot", run.TestCollectionName);
    }

    [Fact]
    public void AnOtherApplicationIsNotAffectedAndTheSiteSettingsAreNotChanged()
    {
        var settings = Site();
        settings.DistributionOverrides[App] = new ApplicationDistribution { DpGroupName = "Pilot DPs" };
        Assert.Equal("Site DPs", ContentDistributionResolver.For(settings, "catalog:bbbbbbbbbbbbbbbb").DpGroupName);
        Assert.Equal("Site DPs", ContentDistributionResolver.For(settings, null).DpGroupName);
        ContentDistributionResolver.For(settings, App);
        Assert.Equal("Site DPs", settings.ContentDistribution.DpGroupName);
    }

    [Fact]
    public void AGroupOrACollectionOfTheApplicationAlsoTurnsTheStepOnForIt()
    {
        var settings = new AppSettings();
        Assert.False(ContentDistributionResolver.For(settings, App).AutoDistribute);
        settings.DistributionOverrides[App] = new ApplicationDistribution { DpGroupName = "Pilot DPs", TestCollectionName = "Own pilot" };
        var run = ContentDistributionResolver.For(settings, App);
        Assert.True(run.AutoDistribute);
        Assert.True(run.DeployToTestCollection);
    }

    [Fact]
    public void TheColumnsWriteAndClearTheValueOfTheApplication()
    {
        var work = new ApplicationWork();
        var row = new SupersedenceRow { Uid = "u", ApplicationId = App, DefinitionId = "d", Application = "A", Vendor = "V" };
        var dp = ApplicationColumns.Find("dpgroup")!;
        var test = ApplicationColumns.Find("testcollection")!;
        Assert.True(dp.Set(work, row, " Pilot DPs "));
        Assert.True(test.Set(work, row, "Own pilot"));
        Assert.Equal("Pilot DPs", dp.Get(work, row));
        Assert.Equal("Own pilot", test.Get(work, row));
        Assert.Contains(App, work.DistributionTouched);
        dp.Set(work, row, "");
        test.Set(work, row, "");
        Assert.Empty(work.Distribution);
    }

    [Fact]
    public void TheDetectionColumnShowsWhetherTheWorkbenchHoldsARuleAndIsNeverEdited()
    {
        var row = new SupersedenceRow { Uid = "u", ApplicationId = App, DefinitionId = "d", Application = "A", Vendor = "V" };
        var work = new ApplicationWork();
        var column = ApplicationColumns.Find("detection")!;
        Assert.Equal("Inherit", column.Get(work, row));
        Assert.NotNull(column.NotApplicable(work, row));
        Assert.False(column.Set(work, row, "Inherit"));

        work.ProfileOf = _ => new GridProfileFields.State(GridProfileFields.State.Empty.Fields, null, CustomDetection: true);
        Assert.Equal("Custom in Workbench", column.Get(work, row));
        Assert.Contains("Workbench", column.NotApplicable(work, row)!);
    }
}
