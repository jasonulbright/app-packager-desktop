using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The install parameters the M365, SSMS and TeamViewer Host definitions declare,
/// and the engine paths they drive: ODT config resolution, uninstall fragments,
/// detection tokens and the gated post-install script.
/// </summary>
public class InstallParameterPortTests
{
    private static string[] Switches(string args) =>
        args.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("m365apps-x64.json")]
    [InlineData("m365apps-x86.json")]
    [InlineData("m365project-x64.json")]
    [InlineData("m365project-x86.json")]
    [InlineData("m365visio-x64.json")]
    [InlineData("m365visio-x86.json")]
    public void M365Defaults_ReproduceTheDefinitionExcludes(string definition)
    {
        var def = DefinitionFixture.Load(definition);

        var resolved = StagingService.ResolveOdtConfig(def, null);

        Assert.Equal(def.Odt!.ExcludeApps.OrderBy(x => x), resolved.ExcludeApps.OrderBy(x => x));
        Assert.Equal("Offline", resolved.DeploymentMode);
        Assert.Equal(def.Odt.Channel, resolved.Channel);
    }

    [Fact]
    public void M365Choices_DriveChannelModeAndExcludes()
    {
        var def = DefinitionFixture.Load("m365apps-x64.json");
        var values = new Dictionary<string, string>
        {
            [StagingService.OdtChannelKey] = "Current",
            [StagingService.OdtDeployModeKey] = "Online",
            ["Exclude_Teams"] = "false",
            ["Exclude_Publisher"] = "true"
        };

        var resolved = StagingService.ResolveOdtConfig(def, values);

        Assert.Equal("Current", resolved.Channel);
        Assert.Equal("Online", resolved.DeploymentMode);
        Assert.DoesNotContain("Teams", resolved.ExcludeApps);
        Assert.Contains("Publisher", resolved.ExcludeApps);
        Assert.Contains("Groove", resolved.ExcludeApps);

        var online = StagingService.GenerateOdtXml("64", "16.0.18000.20000", resolved, null, pinVersion: false);
        var offline = StagingService.GenerateOdtXml("64", "16.0.18000.20000", resolved, @"C:\src", pinVersion: true);
        Assert.DoesNotContain("Version=", online);
        Assert.Contains("Channel=\"Current\"", online);
        Assert.Contains("<ExcludeApp ID=\"Publisher\" />", online);
        Assert.DoesNotContain("<ExcludeApp ID=\"Teams\" />", online);
        Assert.Contains("Version=\"16.0.18000.20000\"", offline);
        Assert.Contains("SourcePath=\"C:\\src\"", offline);
    }

    [Fact]
    public void Ssms_DefaultsReproduceTheQuietInstall()
    {
        var def = DefinitionFixture.Load("ssms.json");

        var install = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null);
        var uninstall = StagingService.ResolveUninstallArgs(
            StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, null)!, def.InstallerSettings, null);
        var detection = StagingService.ApplySettingTokens(def.Detection, def.InstallerSettings, null);

        Assert.Equal(Switches("--norestart --quiet --downloadThenInstall --removeOos true"), Switches(install));
        Assert.Equal(@"uninstall --installPath ""C:\Program Files\Microsoft SQL Server Management Studio 22\Release"" --quiet --norestart", uninstall.Trim());
        Assert.Equal(@"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE", detection.FilePath);
    }

    [Fact]
    public void Ssms_ChoicesMoveThePathAndSwitches()
    {
        var def = DefinitionFixture.Load("ssms.json");
        var values = new Dictionary<string, string>
        {
            ["UIMode"] = "Passive",
            ["InstallPath"] = @"D:\Tools\SSMS",
            ["DownloadThenInstall"] = "false",
            ["IncludeRecommended"] = "true",
            ["ForceClose"] = "true"
        };

        var install = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);
        var command = StagingService.ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, values)!;
        var detection = StagingService.ApplySettingTokens(def.Detection, def.InstallerSettings, values);

        Assert.Equal(Switches(@"--norestart --passive --installPath ""D:\Tools\SSMS"" --includeRecommended --removeOos true --force"), Switches(install));
        Assert.Contains(@"uninstall --installPath ""D:\Tools\SSMS"" --passive --norestart --force", command);
        Assert.DoesNotContain("{setting:", command);
        Assert.Equal(@"D:\Tools\SSMS\Common7\IDE", detection.FilePath);
    }

    [Fact]
    public void TeamViewerHost_AssignmentScriptOnlyWhenAsked()
    {
        var def = DefinitionFixture.Load("teamviewerhost.json");

        Assert.Null(StagingService.ResolvePostInstallScript(def, null));

        var script = StagingService.ResolvePostInstallScript(def, new Dictionary<string, string>
        {
            ["AssignDevice"] = "true",
            ["AssignmentId"] = "0001CoAExample",
            ["AssignmentOptions"] = "--grant-easy-access --reassign"
        });

        Assert.NotNull(script);
        Assert.Contains("assignment --id 0001CoAExample --grant-easy-access --reassign", script);
        Assert.DoesNotContain("{setting:", script);
        Assert.Equal("/S", StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null));
    }

    [Fact]
    public void EveryParameterKeyIsUniqueWithinItsDefinition()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            var def = DefinitionFixture.Load(Path.GetFileName(file));
            if (def.InstallerSettings is not { Length: > 0 }) continue;
            var duplicates = def.InstallerSettings.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
            var blank = def.InstallerSettings.Where(p => string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Label)).Select(_ => "(blank key or label)");
            offenders.AddRange(duplicates.Concat(blank).Select(d => $"{Path.GetFileName(file)}: {d}"));
        }
        Assert.Empty(offenders);
    }
}
