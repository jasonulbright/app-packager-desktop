using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class DeploymentTypeSpecResolverTests
{
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest BaseManifest() => new()
    {
        AppName = "App",
        SoftwareVersion = "1.0",
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Vendor\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0"
        }
    };

    [Fact]
    public void NoDeploymentTypes_YieldsSingleBaseSpec()
    {
        var specs = DeploymentTypeSpecResolver.Resolve(BaseManifest(), Content, "App");

        var spec = Assert.Single(specs);
        Assert.Equal("App", spec.DtName);
        Assert.Equal(Content, spec.ContentLocation);
        Assert.Equal("install.bat", spec.InstallCommand);
        Assert.Equal("uninstall.bat", spec.UninstallCommand);
        Assert.Empty(spec.Requirements);
        Assert.False(DeploymentTypeSpecResolver.IsMultiDeploymentType(specs, "App"));
    }

    [Fact]
    public void Entries_ResolveNameContentCommandsAndRequirementsInOrder()
    {
        var manifest = BaseManifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType
            {
                NameSuffix = "ARM64",
                ContentSubpath = "arm64",
                InstallCommandLine = "Invoke-AppDeployToolkit.exe -DeploymentType Install",
                UninstallCommandLine = "Invoke-AppDeployToolkit.exe -DeploymentType Uninstall",
                Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }]
            },
            new StageDeploymentType { NameSuffix = "x64" }
        ];

        var specs = DeploymentTypeSpecResolver.Resolve(manifest, Content, "App");

        Assert.Equal(2, specs.Count);
        Assert.Equal("App - ARM64", specs[0].DtName);
        Assert.Equal(Path.Combine(Content, "arm64"), specs[0].ContentLocation);
        Assert.Equal("Invoke-AppDeployToolkit.exe -DeploymentType Install", specs[0].InstallCommand);
        Assert.Equal("Invoke-AppDeployToolkit.exe -DeploymentType Uninstall", specs[0].UninstallCommand);
        Assert.Equal("cpu-arch", Assert.Single(specs[0].Requirements).ConditionId);

        // A rule-less entry stays an unconditional fallback on the base content.
        Assert.Equal("App - x64", specs[1].DtName);
        Assert.Equal(Content, specs[1].ContentLocation);
        Assert.Equal("install.bat", specs[1].InstallCommand);
        Assert.Empty(specs[1].Requirements);
        Assert.True(DeploymentTypeSpecResolver.IsMultiDeploymentType(specs, "App"));
    }

    [Fact]
    public void Entries_InheritBaseDetectionAndOverrideIt()
    {
        var manifest = BaseManifest();
        var ownDetection = new StageDetection
        {
            Type = "File",
            FilePath = @"C:\Program Files\App",
            FileName = "app.exe"
        };
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "ARM64", Detection = ownDetection },
            new StageDeploymentType { NameSuffix = "x64" }
        ];

        var specs = DeploymentTypeSpecResolver.Resolve(manifest, Content, "App");

        Assert.Same(ownDetection, specs[0].Detection);
        Assert.Same(manifest.Detection, specs[1].Detection);
    }

    [Fact]
    public void Entry_WithoutNameSuffix_Throws()
    {
        var manifest = BaseManifest();
        manifest.DeploymentTypes = [new StageDeploymentType { NameSuffix = "  " }];

        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentTypeSpecResolver.Resolve(manifest, Content, "App"));
        Assert.Contains("NameSuffix", ex.Message);
    }

    [Fact]
    public void Entries_WithDuplicateNames_Throw()
    {
        var manifest = BaseManifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "x64" },
            new StageDeploymentType { NameSuffix = "X64" }
        ];

        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentTypeSpecResolver.Resolve(manifest, Content, "App"));
        Assert.Contains("duplicate deployment type name", ex.Message);
    }

    [Fact]
    public void Entry_WithNoDetectionAnywhere_Throws()
    {
        var manifest = BaseManifest();
        manifest.Detection = new StageDetection();
        manifest.DeploymentTypes = [new StageDeploymentType { NameSuffix = "x64" }];

        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentTypeSpecResolver.Resolve(manifest, Content, "App"));
        Assert.Contains("no Detection", ex.Message);
    }

    [Fact]
    public void RuleLessEntry_HasNoPhantomRequirement()
    {
        var manifest = BaseManifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "x64", Requirements = null }
        ];

        var specs = DeploymentTypeSpecResolver.Resolve(manifest, Content, "App");
        Assert.Empty(specs[0].Requirements);
    }

    [Fact]
    public void CommandOverrides_ReplaceSingleDeploymentTypeCommands()
    {
        var specs = DeploymentTypeSpecResolver.Resolve(BaseManifest(), Content, "App");
        var log = new List<string>();

        DeploymentTypeSpecResolver.ApplyCommandOverrides(
            specs, new StageCommandOverrides { Install = " setup.exe /s ", Uninstall = "" }, "App", log.Add);

        Assert.Equal("setup.exe /s", specs[0].InstallCommand);
        Assert.Equal("uninstall.bat", specs[0].UninstallCommand);
        Assert.Contains(log, l => l.Contains("(override)"));
    }

    [Fact]
    public void CommandOverrides_AreRefusedOnMultiDeploymentTypeManifests()
    {
        var manifest = BaseManifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "ARM64" },
            new StageDeploymentType { NameSuffix = "x64" }
        ];
        var specs = DeploymentTypeSpecResolver.Resolve(manifest, Content, "App");
        var log = new List<string>();

        DeploymentTypeSpecResolver.ApplyCommandOverrides(
            specs, new StageCommandOverrides { Install = "setup.exe /s" }, "App", log.Add);

        Assert.All(specs, s => Assert.Equal("install.bat", s.InstallCommand));
        Assert.Contains(log, l => l.Contains("ignored"));
    }

    [Fact]
    public void CommandOverrides_WithNeitherCommand_Throw()
    {
        var specs = DeploymentTypeSpecResolver.Resolve(BaseManifest(), Content, "App");

        Assert.Throws<InvalidOperationException>(() => DeploymentTypeSpecResolver.ApplyCommandOverrides(
            specs, new StageCommandOverrides { Install = "", Uninstall = "  " }, "App"));
    }
}
