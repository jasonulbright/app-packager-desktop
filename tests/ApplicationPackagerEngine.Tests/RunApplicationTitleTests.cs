using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

public class RunApplicationTitleTests
{
    private static StageManifest Manifest(string? titleMode = null) => new()
    {
        AppName = "Contoso Tool - 2.1.0 (x64)",
        SoftwareVersion = "2.1.0",
        TitleMode = titleMode
    };

    [Fact]
    public void WithNoTitleSettingTheTitleIsTheNameOfTheManifest()
    {
        Assert.Equal("Contoso Tool - 2.1.0 (x64)", PackagerExecutionService.RunApplicationTitle(Manifest(), null, new AppSettings()));
    }

    [Fact]
    public void TheGlobalSettingToRemoveTheVersionAppliesToAManifestWithoutItsOwnMode()
    {
        var title = PackagerExecutionService.RunApplicationTitle(Manifest(), null, new AppSettings { RemoveVersionFromTitle = true });

        Assert.DoesNotContain("2.1.0", title);
        Assert.StartsWith("Contoso Tool", title);
    }

    [Fact]
    public void TheGlobalSettingToIncludeTheVersionWinsOverTheRemoveSetting()
    {
        var settings = new AppSettings { IncludeVersionInTitle = true, RemoveVersionFromTitle = true };
        var manifest = Manifest();
        manifest.AppName = "Contoso Tool";

        Assert.Contains("2.1.0", PackagerExecutionService.RunApplicationTitle(manifest, null, settings));
    }

    [Fact]
    public void TheModeOfTheManifestOutranksTheGlobalSetting()
    {
        var title = PackagerExecutionService.RunApplicationTitle(Manifest(ApplicationTitle.NoVersionToken), null, new AppSettings { IncludeVersionInTitle = true });

        Assert.DoesNotContain("2.1.0", title);
    }

    [Fact]
    public void TheSavedModeOfTheApplicationOutranksTheGlobalSettingButNotTheManifest()
    {
        var settings = new AppSettings { RemoveVersionFromTitle = true };
        var manifest = Manifest();
        manifest.AppName = "Contoso Tool";

        Assert.Contains("2.1.0", PackagerExecutionService.RunApplicationTitle(manifest, ApplicationTitle.IncludeVersionToken, settings));
        Assert.DoesNotContain("2.1.0", PackagerExecutionService.RunApplicationTitle(Manifest(ApplicationTitle.NoVersionToken), ApplicationTitle.IncludeVersionToken, settings));
    }
}