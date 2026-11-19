using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Systems in Use check boxes derive the stored deployment target, and a command line run with no --target follows them.
/// Every combination of ConfigMgr, Intune and WSUS has its own name; no box is ContentOnly, never ConfigMgr.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SystemsTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-systems-target-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SystemsTargetTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    public static TheoryData<bool, bool, bool, string> Combinations => new()
    {
        { true, false, false, "ConfigMgr" },
        { true, true, false, "ConfigMgrAndIntune" },
        { false, true, false, "IntuneOnly" },
        { true, false, true, "ConfigMgrAndWSUS" },
        { false, false, true, "WSUSOnly" },
        { false, true, true, "IntuneAndWSUS" },
        { true, true, true, "ConfigMgrAndIntuneAndWSUS" },
        { false, false, false, "ContentOnly" },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void TheCheckBoxesNameTheirCombination(bool configMgr, bool intune, bool wsus, string expected) =>
        Assert.Equal(expected, new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }.ToTargetName());

    [Theory]
    [MemberData(nameof(Combinations))]
    public void AFileWithSystemsAndAnEmptyTargetReadsAsTheTargetOfTheCheckBoxes(bool configMgr, bool intune, bool wsus, string expected)
    {
        File.WriteAllText(SettingsService.SettingsPath,
            $$"""{ "Intune": { "DeploymentTarget": "" }, "Systems": { "ConfigMgr": {{configMgr.ToString().ToLowerInvariant()}}, "Intune": {{intune.ToString().ToLowerInvariant()}}, "Wsus": {{wsus.ToString().ToLowerInvariant()}} } }""");

        var settings = new SettingsService().Load();

        Assert.Equal(expected, settings.Intune.DeploymentTarget);
        Assert.Equal(configMgr, settings.Systems!.ConfigMgr);
    }

    [Fact]
    public void AnUnknownStoredTargetNeverBecomesConfigMgr()
    {
        File.WriteAllText(SettingsService.SettingsPath, """{ "Intune": { "DeploymentTarget": "Nonsense" } }""");

        var settings = new SettingsService().Load();

        Assert.Equal(DeploymentTargets.ContentOnly, DeploymentTargets.Normalize(settings.Intune.DeploymentTarget));
    }

    [Fact]
    public void TheCommandLineKnowsWhetherTheTargetWasGiven()
    {
        var without = BuildArguments.Parse(["--application", "catalog:x", "--stage"]);
        var with = BuildArguments.Parse(["--application", "catalog:x", "--stage", "--target", "IntuneAndWSUS"]);

        Assert.False(without.Options!.TargetExplicit);
        Assert.True(with.Options!.TargetExplicit);
        Assert.Equal("IntuneAndWSUS", with.Options.Target);
    }
}
