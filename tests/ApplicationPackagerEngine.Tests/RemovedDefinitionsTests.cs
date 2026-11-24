using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The PowerShell 7 definitions follow the release channels of Microsoft Learn (PowerShell Support Lifecycle): the LTS MSI
/// (7.6), the Stable MSIX and the third-party Devolutions MSI. The definition powershell7lts.json (7.4, out of support since
/// 10 Nov 2026) was removed. A setting or a command-line request that names it keeps working or says why it does not.
/// </summary>
public sealed class RemovedDefinitionsTests
{
    private const string OldId = "000004e27e9117b0";
    private const string NewId = "0000089d8d25e2a0";

    private static WorkbenchApplication App(string id, string source) =>
        new(id, ApplicationIds.ToKey(id), ApplicationOrigin.Catalog, id, null, source, true, ApplicationIds.DefaultProfileId, null, false);

    // ---- the registry --------------------------------------------------------------------------

    [Theory]
    [InlineData("powershell7lts")]
    [InlineData("POWERSHELL7LTS.json")]
    [InlineData("catalog:powershell7lts")]
    [InlineData(@"C:\defs\powershell7lts.json")]
    public void TheRemovedNameIsFoundInEveryFormOfARequest(string text) =>
        Assert.Equal(NewId, RemovedDefinitions.ForName(text)!.ReplacementId);

    [Theory]
    [InlineData("powershell7")]
    [InlineData("powershell7lts2")]
    [InlineData("")]
    [InlineData(null)]
    public void ANameThatWasNotRemovedIsNotFound(string? text) => Assert.Null(RemovedDefinitions.ForName(text));

    [Fact]
    public void TheReplacementIsAShippedDefinition_AndTheRemovedFileIsGone()
    {
        foreach (var entry in RemovedDefinitions.All)
        {
            Assert.Equal(entry.ReplacementId, DefinitionFixture.Load(entry.ReplacementName + ".json").Id);
            Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Definitions", entry.Name + ".json")));
            Assert.NotEqual(entry.Id, entry.ReplacementId);
        }
    }

    [Fact]
    public void TheMessageNamesTheReasonTheEndOfSupportDateAndTheDefinitionToUse()
    {
        var message = RemovedDefinitions.ForName("powershell7lts")!.Message;
        Assert.StartsWith("This definition was removed: PowerShell 7.4 is out of support", message);
        Assert.Contains("10 Nov 2026", message);
        Assert.EndsWith("Use PowerShell 7 LTS.", message);
    }

    [Fact]
    public void ReplaceIdsMovesTheRemovedIdAndKeepsOrderWithoutRepeats()
    {
        var used = new List<RemovedDefinitions.Entry>();
        var result = RemovedDefinitions.ReplaceIds(["a", OldId.ToUpperInvariant(), NewId, "b"], used);

        Assert.Equal(["a", NewId, "b"], result);
        Assert.Single(used);
    }

    [Fact]
    public void ReplaceKeysKeepsTheValueOfTheReplacementWhenBothExist_AndMovesTheValueWhenOnlyTheOldOneExists()
    {
        var both = RemovedDefinitions.ReplaceKeys(new Dictionary<string, int> { [OldId] = 7, [NewId] = 30 }, []);
        Assert.Equal(30, both[NewId]);
        Assert.False(both.ContainsKey(OldId));

        var onlyOld = RemovedDefinitions.ReplaceKeys(new Dictionary<string, int> { [OldId] = 7, ["x"] = 1 }, []);
        Assert.Equal(7, onlyOld[NewId]);
        Assert.Equal(1, onlyOld["x"]);
    }

    // ---- settings --------------------------------------------------------------------------------

    [Fact]
    public void EverySettingThatNamedTheRemovedDefinitionNowNamesItsReplacement()
    {
        var settings = new AppSettings
        {
            HiddenApplications = [OldId, "other"],
            DiscoveryDismissed = [OldId],
            DiscoveryEnabled = [],
            AppStates = new Dictionary<string, AppCheckState> { [OldId] = new() },
            CommandOverrides = new Dictionary<string, CommandOverrideEntry> { [OldId] = new() }
        };
        settings.OneClick.TrackedApplications = [OldId];
        settings.OneClick.CadenceOverrides = new Dictionary<string, int> { [OldId] = 14 };

        var notice = RemovedDefinitionSettings.Apply(settings);

        Assert.Equal([NewId, "other"], settings.HiddenApplications);
        Assert.Equal([NewId], settings.DiscoveryDismissed);
        Assert.Equal([NewId], settings.OneClick.TrackedApplications);
        Assert.Equal(14, settings.OneClick.CadenceOverrides[NewId]);
        Assert.True(settings.AppStates.ContainsKey(NewId));
        Assert.True(settings.CommandOverrides.ContainsKey(NewId));
        Assert.DoesNotContain(OldId, settings.HiddenApplications);
        Assert.NotNull(notice);
        Assert.Contains("powershell7lts", notice);
        Assert.Contains("Use PowerShell 7 LTS.", notice);
    }

    [Fact]
    public void SettingsWithoutTheRemovedIdChangeNothingAndGiveNoNotice()
    {
        var settings = new AppSettings { HiddenApplications = ["a"] };
        settings.OneClick.TrackedApplications = ["b"];

        Assert.Null(RemovedDefinitionSettings.Apply(settings));
        Assert.Equal(["a"], settings.HiddenApplications);
        Assert.Equal(["b"], settings.OneClick.TrackedApplications);
    }

    // ---- the command line --------------------------------------------------------------------------

    [Theory]
    [InlineData("powershell7lts")]
    [InlineData("catalog:powershell7lts")]
    [InlineData("powershell7lts.json")]
    public void TheCommandLineRefusesTheRemovedDefinitionWithTheReason(string value)
    {
        var ex = Assert.Throws<BuildRefusal>(() => ApplicationResolver.Resolve(value, [App("catalog:powershell7", @"C:\d\powershell7.json")]));
        Assert.Contains("This definition was removed", ex.Message);
        Assert.Contains("Use PowerShell 7 LTS.", ex.Message);
    }

    [Fact]
    public void ACustomDefinitionOfTheSameNameStillResolves()
    {
        var custom = new WorkbenchApplication("custom:powershell7lts", ApplicationIds.ToKey("custom:powershell7lts"), ApplicationOrigin.Custom,
            "custom:powershell7lts", null, @"C:\data\powershell7lts.json", true, ApplicationIds.DefaultProfileId, null, false);
        Assert.Equal("custom:powershell7lts", ApplicationResolver.Resolve("powershell7lts", [custom]));
    }

    // ---- the PowerShell 7 channels -------------------------------------------------------------------

    private const string Releases = """
        [
          {"tag_name":"v7.7.0","assets":[{"name":"PowerShell-7.7.0-win-x64.zip"},{"name":"PowerShell-7.7.0.msixbundle"}]},
          {"tag_name":"v7.6.10","assets":[{"name":"PowerShell-7.6.10-win-x64.msi"},{"name":"PowerShell-7.6.10-win-x64.zip"}]},
          {"tag_name":"v7.6.9","assets":[{"name":"PowerShell-7.6.9-win-x64.msi"}]},
          {"tag_name":"v7.5.11","assets":[{"name":"PowerShell-7.5.11-win-x64.msi"}]},
          {"tag_name":"v7.8.0","assets":[{"name":"PowerShell-7.8.0-win-x64.msi"}]}
        ]
        """;

    [Fact]
    public void TheLtsDefinitionTakesTheNewest76MsiAndNeverAnotherLine()
    {
        var def = DefinitionFixture.Load("powershell7.json");
        var config = def.VersionScraper;

        Assert.Equal("7.6.10", HttpRegexScraper.ExtractVersion(Releases, config.Pattern, config.SelectHighestVersion));
        Assert.Equal("PowerShell 7 LTS (MSI)", def.Application); // the title of the PowerShell app; Check ConfigMgr finds the applications of the sites by CMName
        Assert.Equal("PowerShell 7", def.CMName);
        Assert.Equal(NewId, def.Id);
    }

    [Fact]
    public void TheLtsMsiOptsIntoMicrosoftUpdateAndDetectsTheVersionOrNewer()
    {
        var def = DefinitionFixture.Load("powershell7.json");

        Assert.Contains("USE_MU=1", def.InstallArgs);
        Assert.Contains("ENABLE_MU=1", def.InstallArgs);
        Assert.Equal("GreaterEquals", def.Detection.Operator);
        Assert.True(def.RequireValidSignature);
    }

    [Theory]
    [InlineData("powershell7.json", "PowerShell 7 LTS (MSI)", "Long Term Servicing")]
    [InlineData("powershell7msix.json", "PowerShell 7 Stable (MSIX, per user)", "Release channel: Stable")]
    [InlineData("devolutionspowershell.json", "PowerShell 7 (Devolutions, third party)", "Third party")]
    public void EveryPowerShell7DefinitionNamesItsChannelInTheNameAndTheStageNotes(string file, string name, string channelText)
    {
        var def = DefinitionFixture.Load(file);
        Assert.Equal(name, def.Application);
        Assert.Contains(def.StageNotes!, n => n.Contains(channelText));
        Assert.Contains("microsoft:powershell", def.Cpe);
    }

    [Fact]
    public void TheMsixNotesNameTheLearnLagAndTheLimits()
    {
        var notes = string.Join("\n", DefinitionFixture.Load("powershell7msix.json").StageNotes!);
        Assert.Contains("still lists as a preview", notes);
        Assert.Contains("single-user installation", notes);
        Assert.Contains("does not support PowerShell remoting", notes);
    }

    [Fact]
    public void TheThreeDefinitionsHaveDifferentIdsAndNames()
    {
        var all = new[] { "powershell7.json", "powershell7msix.json", "devolutionspowershell.json" }.Select(DefinitionFixture.Load).ToList();
        Assert.Equal(3, all.Select(d => d.Id).Distinct().Count());
        Assert.Equal(3, all.Select(d => d.Application).Distinct().Count());
        Assert.Equal(3, all.Select(d => d.CMName).Distinct().Count());
    }
}
