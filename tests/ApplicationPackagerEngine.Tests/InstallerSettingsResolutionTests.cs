using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Shipped definitions keep parameter-owned switches out of InstallArgs: resolving a
/// definition's parameters with no user overrides must produce the full effective
/// switch set, base arguments plus parameter defaults.
/// </summary>
public class InstallerSettingsResolutionTests
{
    // The full effective install arguments of each definition with default parameters.
    // Compared as a switch set, since the resolver appends parameter fragments after
    // the base.
    private const string MariaDbArgsBeforeDedup = "/qn /norestart SERVICENAME=MariaDB PORT=3306";

    private const string WacArgsBeforeDedup = "/VERYSILENT /HTTPSPortNumber=443";

    private const string SqlArgsBeforeDedup =
        "/Q /ACTION=Install /IACCEPTSQLSERVERLICENSETERMS /SUPPRESSPRIVACYSTATEMENTNOTICE "
        + "/FEATURES=SQLENGINE /INSTANCENAME=SQLEXPRESS /SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\" "
        + "/SQLSVCSTARTUPTYPE=Automatic /BROWSERSVCSTARTUPTYPE=Disabled /UPDATEENABLED=False";

    [Theory]
    [InlineData("mariadb-server.json", MariaDbArgsBeforeDedup)]
    [InlineData("windowsadmincenter.json", WacArgsBeforeDedup)]
    [InlineData("sqlserver2022express.json", SqlArgsBeforeDedup)]
    public void DefaultResolution_ReproducesThePreDedupInstallArgs(string definition, string expected)
    {
        var def = DefinitionFixture.Load(definition);

        var resolved = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null);

        Assert.Equal(SplitSwitches(expected), SplitSwitches(resolved));
    }

    [Theory]
    [InlineData("mariadb-server.json")]
    [InlineData("windowsadmincenter.json")]
    [InlineData("sqlserver2022express.json")]
    public void DefinitionInstallArgs_DoNotDuplicateAParameterFragment(string definition)
    {
        var def = DefinitionFixture.Load(definition);

        Assert.NotNull(def.InstallerSettings);
        foreach (var param in def.InstallerSettings!)
        {
            var prefix = param.CommandFragment!.Split("{value}")[0];
            Assert.DoesNotContain(prefix, def.InstallArgs, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ChosenValues_OverrideTheDefaults()
    {
        var def = DefinitionFixture.Load("mariadb-server.json");
        var values = new Dictionary<string, string> { ["ServiceName"] = "MariaDB11", ["Port"] = "3307" };

        var resolved = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);

        Assert.Equal("/qn /norestart SERVICENAME=MariaDB11 PORT=3307", resolved);
    }

    [Fact]
    public void OmittedKey_FallsBackToTheParameterDefault()
    {
        var def = DefinitionFixture.Load("mariadb-server.json");
        var values = new Dictionary<string, string> { ["Port"] = "3307" };

        var resolved = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);

        Assert.Equal("/qn /norestart SERVICENAME=MariaDB PORT=3307", resolved);
    }

    [Fact]
    public void EmptyChosenValue_DropsTheFragment()
    {
        var def = DefinitionFixture.Load("mariadb-server.json");
        var values = new Dictionary<string, string> { ["ServiceName"] = "" };

        var resolved = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);

        Assert.DoesNotContain("SERVICENAME=", resolved);
        Assert.Contains("PORT=3306", resolved);
    }

    [Fact]
    public void ToggleSetToFalse_OmitsItsFragment()
    {
        InstallerParameter[] parameters =
        [
            new() { Key = "Quiet", ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/qn" }
        ];

        Assert.Equal("setup.exe /qn", StagingService.ResolveInstallerArgs("setup.exe", parameters, null));
        Assert.Equal("setup.exe", StagingService.ResolveInstallerArgs(
            "setup.exe", parameters, new Dictionary<string, string> { ["Quiet"] = "false" }));
    }

    [Fact]
    public void NoParameters_LeavesTheBaseArgsUntouched()
    {
        Assert.Equal("/qn", StagingService.ResolveInstallerArgs("/qn", null, null));
        Assert.Equal("/qn", StagingService.ResolveInstallerArgs("/qn", [], null));
    }

    [Fact]
    public void SqlUninstallArgs_ResolveTheInstanceToken()
    {
        var def = DefinitionFixture.Load("sqlserver2022express.json");

        Assert.Equal(
            "/Q /ACTION=Uninstall /FEATURES=SQLENGINE /INSTANCENAME=SQLEXPRESS",
            StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, null));

        Assert.Equal(
            "/Q /ACTION=Uninstall /FEATURES=SQLENGINE /INSTANCENAME=SQL2022",
            StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings,
                new Dictionary<string, string> { ["InstanceName"] = "SQL2022" }));
    }

    [Fact]
    public void SqlDetectionKey_FollowsTheInstanceName()
    {
        var def = DefinitionFixture.Load("sqlserver2022express.json");

        var byDefault = StagingService.BuildDetection(def, null, "16.0.1000.6");
        Assert.Equal(
            @"SOFTWARE\Microsoft\Microsoft SQL Server\MSSQL16.SQLEXPRESS\Setup",
            byDefault.RegistryKeyRelative);

        var chosen = StagingService.BuildDetection(def, null, "16.0.1000.6", null, null,
            new Dictionary<string, string> { ["InstanceName"] = "SQL2022" });
        Assert.Equal(
            @"SOFTWARE\Microsoft\Microsoft SQL Server\MSSQL16.SQL2022\Setup",
            chosen.RegistryKeyRelative);
    }

    [Fact]
    public void DetectionWithoutSettingTokens_IsReturnedUnchanged()
    {
        var def = DefinitionFixture.Load("windowsadmincenter.json");

        var detection = StagingService.ApplySettingTokens(def.Detection, def.InstallerSettings, null);

        Assert.Same(def.Detection, detection);
    }

    [Fact]
    public void UnknownSettingToken_IsLeftInPlace()
    {
        InstallerParameter[] parameters =
        [
            new() { Key = "InstanceName", ControlType = "Text", DefaultValue = "SQLEXPRESS", CommandFragment = "/I={value}" }
        ];

        Assert.Equal("/X={setting:Missing}", StagingService.ApplySettingTokens("/X={setting:Missing}", parameters, null));
    }

    /// <summary>
    /// Splits an argument string on whitespace outside double quotes, so a
    /// quoted value containing spaces stays one token.
    /// </summary>
    private static HashSet<string> SplitSwitches(string args)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in args)
        {
            if (ch == '"') inQuotes = !inQuotes;

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return [.. tokens];
    }
}
