using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Q-102 part 2: the supersedence grid of Settings (a row choice, the bulk actions all, checked and none) and its copy of the settings.</summary>
public class SupersedenceGridTests
{
    private const string A = "catalog:aaaaaaaaaaaaaaaa";
    private const string B = "catalog:bbbbbbbbbbbbbbbb";
    private const string C = "catalog:cccccccccccccccc";

    [Fact]
    public void ARowFollowsTheDefaultUntilItHasAModeOfItsOwn()
    {
        var settings = new SupersedenceSettings();
        Assert.Equal(SupersedenceGrid.FollowDefault, SupersedenceGrid.ChoiceOf(settings, A));

        Assert.True(SupersedenceGrid.SetChoice(settings, A, "Replace"));
        Assert.Equal("Replace", SupersedenceGrid.ChoiceOf(settings, A));
        Assert.Equal(SupersedenceMode.Replace, SupersedenceSettingsEditor.EffectiveMode(settings, A));
        Assert.Equal(SupersedenceMode.Off, SupersedenceSettingsEditor.EffectiveMode(settings, B));

        Assert.True(SupersedenceGrid.SetChoice(settings, A, SupersedenceGrid.FollowDefault));
        Assert.Equal(SupersedenceGrid.FollowDefault, SupersedenceGrid.ChoiceOf(settings, A));
        Assert.Empty(settings.TitleOverrides);
    }

    [Fact]
    public void AWordThatIsNotAChoiceChangesNothing()
    {
        var settings = new SupersedenceSettings();
        Assert.False(SupersedenceGrid.SetChoice(settings, A, "Always"));
        Assert.False(SupersedenceGrid.SetChoice(settings, null, "Replace"));
        Assert.Empty(settings.TitleOverrides);
    }

    [Fact]
    public void TheCheckedApplicationsGetTheirOwnModeAndTheOthersKeepTheirs()
    {
        var settings = new SupersedenceSettings();
        SupersedenceGrid.SetChoice(settings, C, "Replace and uninstall");
        foreach (var id in new[] { A, B }) SupersedenceGrid.SetChoice(settings, id, "Replace");

        Assert.Equal("Replace", SupersedenceGrid.ChoiceOf(settings, A));
        Assert.Equal("Replace", SupersedenceGrid.ChoiceOf(settings, B));
        Assert.Equal("Replace and uninstall", SupersedenceGrid.ChoiceOf(settings, C));
        Assert.Equal("Off", settings.Mode);
    }

    [Fact]
    public void AllApplicationsTakeTheModeAsTheDefaultAndNoneIsOffForAll()
    {
        var settings = new SupersedenceSettings();
        SupersedenceGrid.SetChoice(settings, A, "Replace");
        SupersedenceGrid.SetChoice(settings, B, "Replace and uninstall");

        SupersedenceSettingsEditor.SetAll(settings, SupersedenceMode.Replace);
        Assert.Equal("Replace", settings.Mode);
        Assert.Equal(SupersedenceGrid.FollowDefault, SupersedenceGrid.ChoiceOf(settings, A));
        Assert.Equal(SupersedenceMode.Replace, SupersedenceSettingsEditor.EffectiveMode(settings, A));

        SupersedenceSettingsEditor.SetAll(settings, SupersedenceMode.Off);
        Assert.Equal("Off", settings.Mode);
        Assert.All(new[] { A, B, C }, id => Assert.Equal(SupersedenceMode.Off, SupersedenceSettingsEditor.EffectiveMode(settings, id)));
        Assert.All(new[] { A, B, C }, id => Assert.False(SupersedenceSettingsEditor.LocksVersionInTitle(settings, id)));
    }

    [Fact]
    public void TheCopyIsEditedWithoutChangingTheStoredSettings()
    {
        var stored = new SupersedenceSettings { Mode = "Replace", ConfigMgrVersionRule = "Exact" };
        stored.TitleOverrides[A] = new TitleSupersedenceOverride { Mode = "Off", VersionRule = "Exact" };
        stored.AcceptedExactBlocks.Add("x|1.0");

        var copy = SupersedenceGrid.Copy(stored);
        SupersedenceGrid.SetChoice(copy, A, SupersedenceGrid.FollowDefault);
        SupersedenceGrid.SetChoice(copy, B, "Off");

        Assert.Equal("Off", stored.TitleOverrides[A].Mode);
        Assert.False(stored.TitleOverrides.ContainsKey(B));
        Assert.Equal("Exact", copy.ConfigMgrVersionRule);
        Assert.Equal(["x|1.0"], copy.AcceptedExactBlocks);
        // The version rule of an override is kept when only its mode is cleared.
        Assert.Equal("Exact", copy.TitleOverrides[A].VersionRule);
        Assert.Null(copy.TitleOverrides[A].Mode);
    }

    [Fact]
    public void TheChoicesAreTheDefaultTheThreeModesInOrder() =>
        Assert.Equal(["Follow the default", "Off", "Replace", "Replace and uninstall"], SupersedenceGrid.Choices);

    [Fact]
    public void TheGridAndItsBulkButtonsAreOnTheSupersedencePageAndNotOnTheToolbar()
    {
        var root = ApplicationPackagerEngine.Tests.Compliance.CatalogServiceTests.RepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(root, "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml"));
        var page = settings[settings.IndexOf("x:Name=\"page_supersedence\"", StringComparison.Ordinal)..];
        page = page[..page.IndexOf("x:Name=\"page_citrix\"", StringComparison.Ordinal)];
        foreach (var id in new[] { "dgSupersedenceApps", "cboBulkColumn", "cboBulkValue", "btnBulkChecked", "btnBulkAll", "btnBulkNone", "btnCheckAll", "btnCheckNone" })
            Assert.Contains(id, page);
        var packages = File.ReadAllText(Path.Combine(root, "src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.xaml"));
        Assert.DoesNotContain("Supersedence", packages);
    }
}
