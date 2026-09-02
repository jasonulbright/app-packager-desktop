using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Guards the DBeaver scope/AI settings: one definition covers both the
/// machine-wide and the per-user install, and the ini append re-runs on every
/// install because the installer replaces dbeaver.ini each time.
/// </summary>
public sealed class DBeaverInstallScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbeaver-tests-" + Guid.NewGuid().ToString("N"));

    public DBeaverInstallScopeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AppDefinition Def() => DefinitionFixture.Load("dbeaver.json");

    private static Dictionary<string, string> Values(string scope, string disableAi) =>
        new() { ["InstallScope"] = scope, ["DisableAI"] = disableAi };

    /// <summary>
    /// Each call gets its own directory: the generator writes wrappers only when
    /// they are missing, so a shared directory would return the first result.
    /// </summary>
    private string GenerateInstall(AppDefinition def, Dictionary<string, string>? values)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new WrapperGeneratorService().GenerateExeWrappers(
            dir, "dbeaver-ce-25.3.5-windows-x86_64.exe",
            StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values),
            StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, values)!,
            def.InstallerFramework, def.Application,
            StagingService.ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, values),
            StagingService.ResolvePostInstallScript(def, values));
        return File.ReadAllText(Path.Combine(dir, "install.ps1"));
    }

    // ── Scope ───────────────────────────────────────────────────

    [Fact]
    public void SystemScope_IsTheDefault()
    {
        var def = Def();

        Assert.Equal("/allusers /S", StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null));
        Assert.Equal("/allusers /S", StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, null));
        Assert.Contains(@"C:\Program Files\DBeaver",
            StagingService.ApplySettingTokens(def.Detection, def.InstallerSettings, null).FilePath);
    }

    [Fact]
    public void UserScope_SwitchesArgsUninstallAndDetection()
    {
        var def = Def();
        var values = Values("User", "false");

        Assert.Equal("/S /currentuser", StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values));
        Assert.Equal("/currentuser /S", StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, values));
        Assert.Equal(@"%LOCALAPPDATA%\DBeaver",
            StagingService.ApplySettingTokens(def.Detection, def.InstallerSettings, values).FilePath);
    }

    [Fact]
    public void UninstallCommand_TargetsTheScopedUninstaller()
    {
        var def = Def();

        var system = StagingService.ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, null)!;
        Assert.Contains(@"$env:ProgramFiles\DBeaver\Uninstall.exe", system);
        Assert.Contains("'/allusers /S'", system);

        var user = StagingService.ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, Values("User", "false"))!;
        Assert.Contains(@"$env:LOCALAPPDATA\DBeaver\Uninstall.exe", user);
        Assert.Contains("'/currentuser /S'", user);
    }

    [Fact]
    public void NoSettingTokenIsLeftUnresolved()
    {
        var def = Def();
        foreach (var values in new[] { Values("System", "true"), Values("User", "true") })
        {
            Assert.DoesNotContain("{setting:", GenerateInstall(def, values));
            Assert.DoesNotContain("{setting:",
                StagingService.ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, values));
        }
    }

    // ── Value maps ──────────────────────────────────────────────

    [Fact]
    public void UnmappedValue_SubstitutesItself()
    {
        InstallerParameter[] parameters =
        [
            new()
            {
                Key = "Scope",
                ControlType = "Dropdown",
                DefaultValue = "System",
                CommandFragment = "{value}",
                ValueMap = new Dictionary<string, string> { ["System"] = "/allusers" }
            }
        ];

        Assert.Equal("/allusers", StagingService.ResolveInstallerArgs("", parameters, null));
        Assert.Equal("Other", StagingService.ResolveInstallerArgs(
            "", parameters, new Dictionary<string, string> { ["Scope"] = "Other" }));
    }

    [Fact]
    public void NamedMapToken_IsIndependentOfTheUnnamedOne()
    {
        InstallerParameter[] parameters =
        [
            new()
            {
                Key = "Scope",
                ControlType = "Dropdown",
                DefaultValue = "System",
                ValueMap = new Dictionary<string, string> { ["System"] = "/allusers" },
                ValueMaps = new Dictionary<string, Dictionary<string, string>>
                {
                    ["Root"] = new() { ["System"] = @"C:\Program Files\App" }
                }
            }
        ];

        Assert.Equal(@"/allusers C:\Program Files\App",
            StagingService.ApplySettingTokens("{setting:Scope} {setting:Scope:Root}", parameters, null));
    }

    // ── Post-install ini append ─────────────────────────────────

    [Fact]
    public void DisableAiOff_LeavesTheInstallWrapperWithoutThePostInstallBlock()
    {
        var install = GenerateInstall(Def(), Values("System", "false"));

        Assert.DoesNotContain("-Dai.disabled=true", install);
        Assert.DoesNotContain("dbeaver.ini", install);
        Assert.EndsWith("exit $proc.ExitCode", install.TrimEnd());
    }

    [Fact]
    public void DisableAiOff_IsTheDefault()
    {
        Assert.Null(StagingService.ResolvePostInstallScript(Def(), null));
    }

    [Fact]
    public void DisableAiOn_AppendsTheIniBlockBehindAnExitCodeGuard()
    {
        var install = GenerateInstall(Def(), Values("System", "true"));

        var guard = install.IndexOf("if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }", StringComparison.Ordinal);
        var append = install.IndexOf("-Dai.disabled=true", StringComparison.Ordinal);
        Assert.True(guard >= 0, "install.ps1 has no installer exit-code guard");
        Assert.True(append > guard, "the ini append is not behind the exit-code guard");
        Assert.EndsWith("exit 0", install.TrimEnd());
    }

    [Fact]
    public void DisableAiOn_WritesTheIniUnderTheScopedInstallRoot()
    {
        Assert.Contains(@"$iniPath = Join-Path ""$env:ProgramFiles\DBeaver"" 'dbeaver.ini'",
            GenerateInstall(Def(), Values("System", "true")));
        Assert.Contains(@"$iniPath = Join-Path ""$env:LOCALAPPDATA\DBeaver"" 'dbeaver.ini'",
            GenerateInstall(Def(), Values("User", "true")));
    }

    [Fact]
    public void TheIniAppendIsIdempotentAndKeepsTheOptionAfterVmargs()
    {
        var install = GenerateInstall(Def(), Values("System", "true"));

        // Re-runs on every install because the installer replaces dbeaver.ini,
        // so the block must skip a line it already wrote.
        Assert.Contains("if (-not ($lines | Where-Object { $_.Trim() -eq $aiOption })) {", install);
        Assert.Contains("if (-not ($lines | Where-Object { $_.Trim() -eq '-vmargs' })) { $lines += '-vmargs' }", install);
        Assert.Contains("$lines += $aiOption", install);
        Assert.Contains("if (Test-Path -LiteralPath $iniPath) {", install);

        var vmargs = install.IndexOf("$lines += '-vmargs'", StringComparison.Ordinal);
        var option = install.IndexOf("$lines += $aiOption", StringComparison.Ordinal);
        Assert.True(vmargs < option, "-vmargs must be appended before the -D option");
    }

    [Fact]
    public void GateOnAnUnknownSetting_SuppressesTheScript()
    {
        var def = Def();
        def.PostInstallScript!.WhenSetting = "NotAParameter";

        Assert.Null(StagingService.ResolvePostInstallScript(def, Values("System", "true")));
    }
}
