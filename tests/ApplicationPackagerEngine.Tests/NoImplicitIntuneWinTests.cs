using System.Reflection;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The presence of IntuneWinAppUtil.exe on a computer must never decide whether a .intunewin bundle is built. Only an Intune
/// publish builds one. A stage and a ConfigMgr-only run build nothing and copy nothing extra to the content share.
/// </summary>
public sealed class NoImplicitIntuneWinTests
{
    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    [Theory]
    [InlineData(typeof(IntuneDeploymentService), "BuildAtStageAsync")]
    [InlineData(typeof(IntuneDeploymentService), "BuildBesideContentAsync")]
    [InlineData(typeof(PackagerExecutionService), "BuildIntuneWinAtStageAsync")]
    public void TheAutomaticBundleBuildsDoNotExist(Type type, string name) =>
        Assert.Null(type.GetMethod(name, AllMembers));

    [Fact]
    public void NoRunPathAsksWhetherTheContentPrepToolIsInstalled()
    {
        var folder = FindServicesFolder();
        var offenders = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("IntuneDeploymentService.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("InstalledToolPath", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheIntuneDeploymentServiceKeepsTheToolLookupOnlyForTheIntunePublishAndSettings()
    {
        var source = File.ReadAllText(Path.Combine(FindServicesFolder(), "IntuneDeploymentService.cs"));
        var uses = source.Split("InstalledToolPath", StringSplitOptions.None).Length - 1;

        // The definition and the Settings page only; no caller in this file decides on the tool.
        Assert.Equal(1, uses);
    }

    private static string FindServicesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ApplicationPackagerUI", "Services");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/ApplicationPackagerUI/Services was not found above the test folder.");
    }
}
