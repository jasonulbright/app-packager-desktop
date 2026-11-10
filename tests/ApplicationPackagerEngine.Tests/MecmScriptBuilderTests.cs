using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class MecmScriptBuilderTests
{
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest Manifest() => new()
    {
        AppName = "App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Vendor\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0"
        }
    };

    private static string Build(StageManifest manifest, StageCommandOverrides? overrides = null,
        Action<string>? onLog = null, IReadOnlyList<StageRequirement>? requirements = null,
        string? providerMachine = null) =>
        MecmTarget.BuildPackageScript(manifest, manifest.AppName, Content, "P01", "comment",
            15, 30, ConditionTemplateService.Defaults(), overrides, onLog, requirements, providerMachine);

    [Fact]
    public void SingleDeploymentType_CreatesOneDtNamedAfterTheApp()
    {
        var script = Build(Manifest());

        Assert.Contains("$dtName0 = 'App'", script);
        Assert.DoesNotContain("$dtName1", script);
        Assert.Contains($"-ContentLocation '{Content}'", script);
        Assert.Contains("-InstallCommand 'install.bat' -UninstallCommand 'uninstall.bat'", script);
        Assert.Contains("-InstallationBehaviorType InstallForSystem", script);
        Assert.DoesNotContain("-AddRequirement", script);
        // No requirement rules means the global condition helper is not needed.
        Assert.DoesNotContain(RequirementRuleBuilder.GlobalConditionFunctionName, script);
    }

    [Fact]
    public void UnchangedVersionRerun_ValidatesEveryExpectedDtNameAndExitsClean()
    {
        var manifest = Manifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "ARM64" },
            new StageDeploymentType { NameSuffix = "x64" }
        ];

        var script = Build(manifest);

        Assert.Contains("$expectedDts = @('App - ARM64', 'App - x64')", script);
        Assert.Contains("$missingDts = @($expectedDts | Where-Object { $currentDts -notcontains $_ })", script);
        Assert.Contains("if ($missingDts.Count -eq 0) {", script);
        Assert.Contains("exit 0", script);
        Assert.Contains("missing deployment type(s)", script);
    }

    [Fact]
    public void VersionChange_StagesTheNewSetThenRemovesOldThenRenames()
    {
        var manifest = Manifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "ARM64" },
            new StageDeploymentType { NameSuffix = "x64" }
        ];

        var script = Build(manifest);

        Assert.Contains("$dtCreateName0 = if ($replaceDtNames.Count -gt 0) { \"$dtName0 (staging)\" } else { $dtName0 }", script);
        var lastAdd = script.LastIndexOf("Add-CMScriptDeploymentType", StringComparison.Ordinal);
        var remove = script.IndexOf("Remove-CMDeploymentType -ApplicationName 'App' -DeploymentTypeName $oldDt", StringComparison.Ordinal);
        var rename = script.IndexOf("-NewDeploymentTypeName", StringComparison.Ordinal);
        Assert.True(lastAdd < remove, "every deployment type is created before any old one is removed");
        Assert.True(remove < rename, "old deployment types are removed before the staged names are taken");
        Assert.Contains("Set-CMApplication -Name 'App' -SoftwareVersion '1.0'", script);
    }

    [Fact]
    public void Requirements_AttachPerDeploymentType_AndFallbackStaysUnconditional()
    {
        var manifest = Manifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType
            {
                NameSuffix = "ARM64",
                ContentSubpath = "arm64",
                Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }]
            },
            new StageDeploymentType { NameSuffix = "x64" }
        ];

        var script = Build(manifest);

        Assert.Contains(RequirementRuleBuilder.GlobalConditionFunctionName, script);
        Assert.Contains("-Value1 '12'", script);
        Assert.Contains("-AddRequirement @($dt0_req0)", script);
        Assert.DoesNotContain("$dt1_req0", script);
        Assert.Contains($"-ContentLocation '{Path.Combine(Content, "arm64")}'", script);
        // Rules are resolved before the application is created.
        Assert.True(
            script.IndexOf("$dt0_req0 =", StringComparison.Ordinal) <
            script.IndexOf("New-CMApplication", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownConditionId_ThrowsBeforeAnyScriptIsProduced()
    {
        var manifest = Manifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType
            {
                NameSuffix = "x64",
                Requirements = [new StageRequirement { ConditionId = "bogus", Value = "1" }]
            }
        ];

        Assert.Throws<InvalidOperationException>(() => Build(manifest));
    }

    [Fact]
    public void ManifestCommandOverrides_ApplyToTheSingleDeploymentType()
    {
        var manifest = Manifest();
        manifest.CommandOverrides = new StageCommandOverrides { Install = "setup.exe /s" };
        var log = new List<string>();

        var script = Build(manifest, onLog: log.Add);

        Assert.Contains("-InstallCommand 'setup.exe /s' -UninstallCommand 'uninstall.bat'", script);
        Assert.Contains(log, l => l.Contains("(override)"));
    }

    [Fact]
    public void ExplicitOverrideParameter_WinsOverTheManifestRecord()
    {
        var manifest = Manifest();
        manifest.CommandOverrides = new StageCommandOverrides { Install = "manifest.exe" };

        var script = Build(manifest, new StageCommandOverrides { Install = "explicit.exe" });

        Assert.Contains("-InstallCommand 'explicit.exe'", script);
        Assert.DoesNotContain("manifest.exe", script);
    }

    [Fact]
    public void CommandOverrides_AreRefusedOnMultiDeploymentTypeManifests()
    {
        var manifest = Manifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "ARM64" },
            new StageDeploymentType { NameSuffix = "x64" }
        ];
        var log = new List<string>();

        var script = Build(manifest, new StageCommandOverrides { Install = "setup.exe /s" }, log.Add);

        Assert.DoesNotContain("setup.exe /s", script);
        Assert.Contains(log, l => l.Contains("ignored"));
    }

    [Fact]
    public void NoProviderMachine_LeavesTheDriveRootOnTheLocalMachine()
    {
        var script = Build(Manifest());

        Assert.Contains("-PSProvider CMSite -Root $env:COMPUTERNAME", script);
    }

    [Fact]
    public void ProviderMachine_BecomesTheCmSiteDriveRoot()
    {
        var script = Build(Manifest(), providerMachine: "cm01.contoso.com");

        Assert.Contains("-PSProvider CMSite -Root 'cm01.contoso.com'", script);
        Assert.DoesNotContain("$env:COMPUTERNAME", script);
    }

    [Fact]
    public void ExplicitRequirements_AttachToTheSingleDeploymentType()
    {
        var script = Build(Manifest(), requirements:
        [
            new StageRequirement { ConditionId = "cpu-arch", Value = "x64" },
            new StageRequirement { ConditionId = "vpn-connected", Value = true }
        ]);

        Assert.Contains("-Value1 '9'", script);
        Assert.Contains("New-CMRequirementRuleBooleanValue", script);
        Assert.Contains("-AddRequirement @($dt0_req0, $dt0_req1)", script);
    }

    [Fact]
    public void ExplicitRequirements_AreRefusedOnMultiDeploymentTypeManifests()
    {
        var manifest = Manifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType
            {
                NameSuffix = "ARM64",
                Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }]
            },
            new StageDeploymentType { NameSuffix = "x64" }
        ];
        var log = new List<string>();

        var script = Build(manifest, onLog: log.Add,
            requirements: [new StageRequirement { ConditionId = "vpn-connected", Value = true }]);

        // The variant entries keep their own rules and the fallback stays unconditional.
        Assert.DoesNotContain("New-CMRequirementRuleBooleanValue", script);
        Assert.Contains(log, l => l.Contains("Requirement rules ignored"));
    }

    private static StageManifest MsixManifest()
    {
        var manifest = Manifest();
        manifest.InstallerType = "MSIX";
        manifest.Detection = StagingService.BuildMsixScriptDetection(new StageMsixPackage
        {
            Name = "ohmyposh.cli",
            Version = "31.1.1.0",
            ProcessorArchitecture = "x64"
        });
        return manifest;
    }

    [Fact]
    public void ScriptDetection_PassesScriptTextInsteadOfADetectionClause()
    {
        var script = Build(MsixManifest());

        Assert.Contains("$detectScript0 = @'", script);
        Assert.Contains("Get-AppxProvisionedPackage -Online", script);
        Assert.Contains("-ScriptLanguage PowerShell -ScriptText $detectScript0", script);
        Assert.DoesNotContain("-AddDetectionClause", script);
        Assert.DoesNotContain("New-CMDetectionClause", script);
    }

    [Fact]
    public void ScriptDetection_KeepsTheScriptDeploymentTypeWrapperCommands()
    {
        var script = Build(MsixManifest());

        Assert.Contains("Add-CMScriptDeploymentType", script);
        Assert.Contains("-InstallCommand 'install.bat' -UninstallCommand 'uninstall.bat'", script);
        Assert.DoesNotContain("Add-CMWindowsAppxDeploymentType", script);
    }

    [Fact]
    public void ScriptDetection_DoesNotSwitchOffTheSiteDriveForAClause()
    {
        var script = Build(MsixManifest());

        Assert.DoesNotContain("Set-Location C:", script);
    }

    [Fact]
    public void ScriptDetection_WithoutScriptText_Throws()
    {
        var manifest = Manifest();
        manifest.Detection = new StageDetection { Type = "Script", ScriptLanguage = "PowerShell" };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(manifest));
        Assert.Contains("no ScriptText", ex.Message);
    }

    [Fact]
    public void ScriptDetection_WithAnUnsupportedLanguage_Throws()
    {
        var manifest = MsixManifest();
        manifest.Detection.ScriptLanguage = "Python";

        Assert.Throws<InvalidOperationException>(() => Build(manifest));
    }

    [Fact]
    public void ScriptDetection_ContainingAHereStringTerminator_Throws()
    {
        var manifest = Manifest();
        manifest.Detection = new StageDetection
        {
            Type = "Script",
            ScriptLanguage = "PowerShell",
            ScriptText = "Write-Output 'x'\r\n'@\r\nexit 0"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(manifest));
        Assert.Contains("here-string terminator", ex.Message);
    }

    [Fact]
    public void ScriptDetection_SurvivesTheDeploymentTypeSpecResolver()
    {
        var manifest = MsixManifest();
        manifest.DeploymentTypes = [new StageDeploymentType { NameSuffix = "x64" }];

        var script = Build(manifest);

        Assert.Contains("-ScriptText $detectScript0", script);
    }

    [Fact]
    public void UserInstallContext_OmitsLogonRequirement()
    {
        var manifest = Manifest();
        manifest.InstallContext = "User";

        var script = Build(manifest);

        Assert.Contains("-InstallationBehaviorType InstallForUser", script);
        Assert.DoesNotContain("-LogonRequirementType", script);
    }

    [Fact]
    public void StagedIcon_AppliesAfterCreationAndNeverFailsTheRun()
    {
        var manifest = Manifest();
        manifest.Icon = "app-icon.png";

        var script = Build(manifest);

        Assert.Contains($@"$iconPath = '{Content}\app-icon.png'", script);
        Assert.Contains("Set-CMApplication -Name 'App' -IconLocationFile $iconPath -ErrorAction Stop", script);
        Assert.Contains("Application icon not applied", script);
        // The creation-path icon follows every deployment type so a provider
        // refusal cannot leave the application half-built. The earlier
        // occurrence sits inside the idempotent exit-0 branch, which applies
        // the icon to an existing unchanged application.
        Assert.True(script.LastIndexOf("-IconLocationFile", StringComparison.Ordinal)
                  > script.IndexOf("Add-CMScriptDeploymentType", StringComparison.Ordinal));
        Assert.True(script.IndexOf("-IconLocationFile", StringComparison.Ordinal)
                  < script.IndexOf("exit 0", StringComparison.Ordinal));
        // CMSite-drive trap: a bare UNC Test-Path resolves through the CM
        // provider; both checks must be FileSystem-qualified.
        Assert.Contains("FileSystem::$iconPath", script);
        Assert.Contains("FileSystem::$idemIcon", script);
    }

    [Fact]
    public void NoStagedIcon_EmitsNoIconStep()
    {
        Assert.DoesNotContain("-IconLocationFile", Build(Manifest()));
    }
}
