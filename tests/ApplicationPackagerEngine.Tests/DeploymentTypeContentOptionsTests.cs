using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The two deployment type content options reach Add-CMScriptDeploymentType. The
/// defaults produce the arguments every deployment type carried before the options
/// existed; an unknown stored value warns and takes the default.
/// </summary>
public sealed class DeploymentTypeContentOptionsTests
{
    private static StageManifest Manifest() => new()
    {
        AppName = "App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ExpectedValue = "1.0" }
    };

    private static string Build(DeploymentTypeContentOptions? options, Action<string>? log = null) =>
        MecmTarget.BuildPackageScript(Manifest(), "App", @"\\server\share\App\1.0", "P01", "c", 15, 30,
            onLog: log, contentOptions: options);

    [Fact]
    public void TheDefaultsKeepTodaysArguments()
    {
        Assert.Equal(" -ContentFallback -SlowNetworkDeploymentMode Download", DeploymentTypeContentOptions.Default.CmdletArguments);
        Assert.Equal(Build(null), Build(DeploymentTypeContentOptions.Resolve("Allow", "Download")));
        Assert.Contains(" -ContentFallback -SlowNetworkDeploymentMode Download -RebootBehavior BasedOnExitCode", Build(null));
    }

    [Fact]
    public void DenyAndDoNothingReachTheDeploymentType()
    {
        var script = Build(DeploymentTypeContentOptions.Resolve("Deny", "DoNothing"));
        Assert.Contains(" -ContentFallback:$false -SlowNetworkDeploymentMode DoNothing", script);
        Assert.Equal(Build(null).Replace(" -ContentFallback -SlowNetworkDeploymentMode Download", " -ContentFallback:$false -SlowNetworkDeploymentMode DoNothing"), script);
    }

    [Fact]
    public void TheRunLogNamesTheOptions()
    {
        var log = new List<string>();
        Build(DeploymentTypeContentOptions.Resolve("deny", "download"), log.Add);
        Assert.Contains("Deployment type content      : fallback=Deny, slow network=Download", log);
    }

    [Fact]
    public void AnUnknownValueWarnsAndTakesTheDefault()
    {
        var warnings = new List<string>();
        var options = DeploymentTypeContentOptions.Resolve("Sometimes", "Stream", warnings.Add);
        Assert.Equal(DeploymentTypeContentOptions.Default, options);
        Assert.Equal(
            ["Content fallback 'Sometimes' is not Allow or Deny; using Allow.",
             "Slow network deployment mode 'Stream' is not Download or DoNothing; using Download."],
            warnings);
    }

    [Fact]
    public void APs1ChildReadsTheSameOptions()
    {
        var env = DeploymentTypeContentOptions.Resolve("Deny", "DoNothing").EnvironmentValues;
        Assert.Equal("Deny", env[DeploymentTypeContentOptions.FallbackVariable]);
        Assert.Equal("DoNothing", env[DeploymentTypeContentOptions.SlowNetworkVariable]);
    }

    [Theory]
    [InlineData(null, "BasedOnExitCode")]
    [InlineData("", "BasedOnExitCode")]
    [InlineData("NoAction", "NoAction")]
    [InlineData("forcereboot", "ForceReboot")]
    public void ADeploymentTypeWithoutABehaviorRestartsOnItsExitCode(string? behavior, string expected) =>
        Assert.Equal(expected, MecmTarget.ResolveRebootBehavior(behavior));

    // ── Network content path ────────────────────────────────────

    [Theory]
    [InlineData(ContentLayouts.Nested, "Contoso Inc.", "App 2. ", "1.0", @"\\fs\sccm$\Applications\Contoso Inc\App 2\1.0")]
    [InlineData(ContentLayouts.Nested, "KDE e.V", "KDiff3 (x64)", "1.12.4", @"\\fs\sccm$\Applications\KDE e.V\KDiff3 (x64)\1.12.4")]
    [InlineData(ContentLayouts.Flat, "Contoso", "App.", "1.0.", @"\\fs\sccm$\Applications\Contoso-App.-1.0")]
    public void TrailingDotsAndSpacesNeverReachAFolderSegment(string layout, string vendor, string app, string version, string expected) =>
        Assert.Equal(expected, ContentLayouts.ContentPath(@"\\fs\sccm$", layout, vendor, app, version));

    [Fact]
    public async Task BareStagingTrimsTheApplicationFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "apkg-bare-" + Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "setup.exe"), "x");
        try
        {
            var manifest = new StageManifest { AppName = "Tool Inc.", Publisher = "Vendor.", SoftwareVersion = "2.0" };
            var result = await new BareStaging().StageToNetworkAsync(manifest, staged, Path.Combine(root, "share"));
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(Path.Combine(root, "share", "Applications", "Vendor", "Tool Inc", "2.0"), result.NetworkContentPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
