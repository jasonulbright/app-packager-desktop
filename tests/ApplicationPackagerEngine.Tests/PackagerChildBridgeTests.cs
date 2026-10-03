using System.Diagnostics;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class PackagerChildBridgeTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private string Script(string name = "package-bridgefixture.ps1") =>
        _t.NewFile(Path.Combine(_t.NewFolder("Packagers"), name), "# packager");

    [Fact]
    public void Prepare_OnTheDefaultProfileKeepsTheDownloadRootAndPassesTheSnapshotAndRoot()
    {
        var downloadRoot = Path.Combine(_t.Root, "dl");
        var launch = PackagerChildBridge.Prepare(_t.Store, Script(), downloadRoot);

        Assert.Equal("catalog:package-bridgefixture", launch.ApplicationId);
        Assert.Equal("default", launch.ProfileId);
        Assert.Equal(downloadRoot, launch.DownloadRoot);
        Assert.Equal(launch.Snapshot.Path, launch.Environment[PackagerChildBridge.RunSnapshotVariable]);
        Assert.Equal(_t.Store.DataRoot, launch.Environment[PackagerChildBridge.WorkbenchRootVariable]);
        Assert.Equal(downloadRoot, launch.Environment[PackagerChildBridge.DownloadRootVariable]);
        Assert.False(launch.Environment.ContainsKey(PackagerChildBridge.CommandsVariable));
        Assert.False(launch.Environment.ContainsKey(PackagerChildBridge.VariantsVariable));
        Assert.True(File.Exists(launch.Snapshot.Path));
    }

    [Fact]
    public void Prepare_OnTheActiveProfileStagesUnderItsOwnRootAndCarriesItsOverrides()
    {
        var script = Script();
        var id = "catalog:package-bridgefixture";
        var profile = WorkbenchProfile.CreateNew(id, name: "Lab");
        profile.Set("Install.Command", "setup.exe /S");
        profile.Set("InstallMode", "CurrentUser");
        profile.Set("Application.TitleMode", "No version");
        profile.Data["Requirements"] = WorkbenchTestRoot.Json("""
            { "Operations": [
                { "Op": "Add", "RuleId": "cpu-arch", "Rule": { "ConditionId": "cpu-arch", "Value": "x64" }, "AppliesTo": ["*"] },
                { "Op": "Add", "RuleId": "lang", "Rule": { "ConditionId": "os-language", "Cultures": ["de-DE"] }, "AppliesTo": ["ARM64"] },
                { "Op": "Remove", "RuleId": "vpn-connected" } ] }
            """);
        profile.Data["Variants"] = WorkbenchTestRoot.Json("""{ "Split": { "SchemaVersion": 1, "Split": "Architecture" }, "Overrides": {} }""");
        profile = _t.Store.SaveProfile(profile, setActive: true);
        var downloadRoot = Path.Combine(_t.Root, "dl");

        var launch = PackagerChildBridge.Prepare(_t.Store, script, downloadRoot, RunTargets.MecmAndIntune);

        Assert.Equal(profile.ProfileId, launch.ProfileId);
        Assert.Equal(Path.Combine(downloadRoot, "profiles", profile.ProfileId), launch.DownloadRoot);
        Assert.Equal(launch.DownloadRoot, launch.Snapshot.DownloadRoot);
        Assert.Equal(RunTargets.MecmAndIntune, launch.Snapshot.Target);
        Assert.Equal(profile.Revision, launch.Snapshot.ProfileRevision);
        var rules = JsonNode.Parse(launch.Environment[PackagerChildBridge.RequirementsVariable])!;
        Assert.Equal(1, rules["SchemaVersion"]!.GetValue<int>());
        Assert.Single(rules["Rules"]!.AsArray());
        Assert.Equal("cpu-arch", rules["Rules"]![0]!["ConditionId"]!.GetValue<string>());
        Assert.Equal("Architecture", JsonNode.Parse(launch.Environment[PackagerChildBridge.VariantsVariable])!["Split"]!.GetValue<string>());
        var commands = JsonNode.Parse(launch.Environment[PackagerChildBridge.CommandsVariable])!.AsObject();
        Assert.Equal("setup.exe /S", commands["Install"]!.GetValue<string>());
        Assert.False(commands.ContainsKey("Uninstall"));
        Assert.Equal("CurrentUser", launch.Environment[PackagerChildBridge.InstallModeVariable]);
        Assert.Equal("NoVersion", launch.Environment[PackagerChildBridge.TitleModeVariable]);
    }

    [Fact]
    public void LegacyVariables_FallBackToTheGlobalIncludeVersionPreference()
    {
        var profile = WorkbenchProfile.CreateDefault("catalog:package-x");
        Assert.False(PackagerChildBridge.BuildLegacyVariables(profile).ContainsKey(PackagerChildBridge.TitleModeVariable));
        Assert.Equal("IncludeVersion", PackagerChildBridge.BuildLegacyVariables(profile, includeVersionInTitle: true)[PackagerChildBridge.TitleModeVariable]);
    }

    [Fact]
    public void Prepare_RefusesAnEnabledSigningPolicyBeforeAnySnapshotIsWritten()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PackagerChildBridge.Prepare(
            _t.Store, Script(), Path.Combine(_t.Root, "dl"), signingPolicy: WorkbenchTestRoot.Json("""{ "SignDetection": true }""")));
        Assert.Equal(NoSigningServiceGate.NotLoadedMessage, ex.Message);
        Assert.False(Directory.Exists(_t.Store.RunsRoot));
    }

    [Fact]
    public void Apply_SetsOnlyNonEmptyValuesOnTheChildEnvironment()
    {
        var startInfo = new ProcessStartInfo("powershell.exe");
        PackagerChildBridge.Apply(startInfo, new Dictionary<string, string>
        {
            [PackagerChildBridge.RunSnapshotVariable] = @"C:\data\runs\b.json",
            [PackagerChildBridge.TitleModeVariable] = ""
        });
        Assert.Equal(@"C:\data\runs\b.json", startInfo.Environment[PackagerChildBridge.RunSnapshotVariable]);
        Assert.NotEqual("", startInfo.Environment.TryGetValue(PackagerChildBridge.TitleModeVariable, out var title) ? title : null);
        Assert.NotEqual(@"C:\data\runs\b.json", Environment.GetEnvironmentVariable(PackagerChildBridge.RunSnapshotVariable));
    }
}
