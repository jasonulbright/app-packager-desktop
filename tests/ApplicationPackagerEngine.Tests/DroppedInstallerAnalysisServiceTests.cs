using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Stands in for the Windows Installer COM API so MSI analysis is exercised
/// without shipping a binary .msi fixture.
/// </summary>
internal sealed class FakeMsiInspector : IMsiInspector
{
    public MsiProperties Properties { get; init; } = new();
    public string TemplateArchitecture { get; init; } = "x64";
    public bool Throws { get; init; }

    public MsiProperties ReadProperties(string msiPath) =>
        Throws ? throw new InvalidOperationException("not an MSI database") : Properties;

    public string ReadTemplateArchitecture(string msiPath) =>
        Throws ? "" : TemplateArchitecture;
}

public class DroppedInstallerAnalysisServiceTests : IDisposable
{
    private const string ProductCode = "{23170F69-40C1-2702-2408-000001000000}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"packr-droptest-{Guid.NewGuid():N}");

    public DroppedInstallerAnalysisServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string WriteFile(string name, string? asciiMarker = null)
    {
        var path = Path.Combine(_root, name);
        var bytes = new List<byte>(Enumerable.Repeat((byte)0x00, 512));
        if (asciiMarker is not null)
            bytes.AddRange(Encoding.ASCII.GetBytes(asciiMarker));
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private static DroppedInstallerAnalysisService MsiService(
        string productName = "7-Zip 24.08 (x64)",
        string publisher = "Igor Pavlov",
        string version = "24.08",
        string productCode = ProductCode,
        string? allUsers = "1",
        string architecture = "x64") =>
        new(new FakeMsiInspector
        {
            TemplateArchitecture = architecture,
            Properties = new MsiProperties
            {
                ProductName = productName,
                Manufacturer = publisher,
                ProductVersion = version,
                ProductCode = productCode,
                AllUsers = allUsers
            }
        });

    private static DroppedInstallerAnalysisService ExeService() =>
        new(new FakeMsiInspector { Throws = true });

    [Fact]
    public async Task Analyze_Msi_IdentityIsConfirmed()
    {
        var path = WriteFile("7z2408-x64.msi");
        var analysis = await MsiService().AnalyzeAsync(path);

        Assert.Equal("NativeMSI", analysis.Engine);
        Assert.Equal("MSI", analysis.InstallerType);
        Assert.Equal("7-Zip 24.08 (x64)", analysis.AppName.Value);
        Assert.Equal("Igor Pavlov", analysis.Publisher.Value);
        Assert.Equal("24.08", analysis.SoftwareVersion.Value);
        Assert.Equal(ProductCode, analysis.ProductCode.Value);
        Assert.Equal(AnalysisConfidence.Confirmed, analysis.Confidence);
        Assert.False(analysis.RequiresConfirmation);
        Assert.All(analysis.Fields().Where(f => f.Field.HasValue),
            f => Assert.Equal(AnalysisConfidence.Confirmed, f.Field.Confidence));
    }

    [Fact]
    public async Task Analyze_Msi_DetectionIsProductCodeArpKey()
    {
        var path = WriteFile("7z2408-x64.msi");
        var analysis = await MsiService().AnalyzeAsync(path);

        Assert.Equal("RegistryKeyValue", analysis.Detection.Type);
        Assert.Equal($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}",
            analysis.Detection.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", analysis.Detection.ValueName);
        Assert.Equal("24.08", analysis.Detection.ExpectedValue);
        Assert.True(analysis.Detection.Is64Bit);
        Assert.Contains(ProductCode, analysis.DetectionHint);
    }

    [Fact]
    public async Task Analyze_Msi_UsesMsiexecSwitches()
    {
        var path = WriteFile("7z2408-x64.msi");
        var analysis = await MsiService().AnalyzeAsync(path);

        Assert.Equal("/qn /norestart", analysis.InstallArgs.Value);
        Assert.Equal("/qn /norestart", analysis.UninstallArgs.Value);
        Assert.Contains(ProductCode, analysis.UninstallRegistryKey.Value);
        Assert.StartsWith("HKLM:", analysis.UninstallRegistryKey.Value);
    }

    [Fact]
    public async Task Analyze_PerUserMsi_PredictsHkcuArpKey()
    {
        var path = WriteFile("peruser.msi");
        var analysis = await MsiService(allUsers: "").AnalyzeAsync(path);

        Assert.StartsWith("HKCU:", analysis.UninstallRegistryKey.Value);
        Assert.False(analysis.Detection.Is64Bit);
    }

    [Fact]
    public async Task Analyze_ThirtyTwoBitMsi_MapsWow6432NodeOntoIs64Bit()
    {
        var path = WriteFile("x86.msi");
        var analysis = await MsiService(architecture: "Intel").AnalyzeAsync(path);

        Assert.Equal("x86", analysis.Architecture);
        Assert.Contains("WOW6432Node", analysis.UninstallRegistryKey.Value);
        Assert.False(analysis.Detection.Is64Bit);
        Assert.DoesNotContain("WOW6432Node", analysis.Detection.RegistryKeyRelative!);
    }

    [Fact]
    public async Task BuildStageManifest_Msi_NeedsNoConfirmation()
    {
        var path = WriteFile("7z2408-x64.msi");
        var service = MsiService();
        var analysis = await service.AnalyzeAsync(path);

        var manifest = service.BuildStageManifest(analysis);

        Assert.Equal("7-Zip 24.08 (x64)", manifest.AppName);
        Assert.Equal("Igor Pavlov", manifest.Publisher);
        Assert.Equal("24.08", manifest.SoftwareVersion);
        Assert.Equal("7z2408-x64.msi", manifest.InstallerFile);
        Assert.Equal("MSI", manifest.InstallerType);
        Assert.Equal(ProductCode, manifest.ProductCode);
        Assert.Equal("Machine", manifest.InstallContext);
        Assert.Contains(ProductCode, manifest.Detection.RegistryKeyRelative!);
    }

    [Fact]
    public async Task BuildStageManifest_PerUserMsi_IsAUserContextWithCurrentUserDetection()
    {
        var path = WriteFile("peruser.msi");
        var service = MsiService(allUsers: "");
        var analysis = await service.AnalyzeAsync(path);

        Assert.Equal("User", analysis.InstallContext);
        Assert.StartsWith("HKCU", analysis.UninstallRegistryKey.Value);

        // The review panel sends overrides without a context; the prediction has to survive them.
        var manifest = service.BuildStageManifest(analysis, new DroppedInstallerOverrides { AppName = "Per User App" });
        Assert.Equal("User", manifest.InstallContext);
        Assert.Equal("CurrentUser", manifest.Detection.Hive);
        Assert.True(manifest.Detection.IsCurrentUserHive);

        var definition = service.BuildDefinition(analysis);
        Assert.Equal("HKCU", definition.Detection.RegistryHive);
        Assert.Equal("User", definition.InstallContext);

        var machine = service.BuildStageManifest(analysis, new DroppedInstallerOverrides { InstallContext = "Machine" });
        Assert.Equal("Machine", machine.InstallContext);
    }

    [Fact]
    public async Task BuildDefinition_Msi_TemplatesTheProductCode()
    {
        var path = WriteFile("7z2408-x64.msi");
        var service = MsiService();
        var analysis = await service.AnalyzeAsync(path);

        var definition = service.BuildDefinition(analysis);

        Assert.Equal(1, definition.Schema);
        Assert.False(string.IsNullOrWhiteSpace(definition.Id));
        Assert.Equal("Igor Pavlov", definition.Vendor);
        Assert.Equal("MSI", definition.InstallerType);
        Assert.Equal("NativeMSI", definition.InstallerFramework);
        Assert.Equal("7z2408-x64.msi", definition.InstallerFileName);
        Assert.Equal("/qn /norestart", definition.InstallArgs);
        Assert.Equal("/x {ProductCode} /qn /norestart", definition.UninstallArgs);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}",
            definition.Detection.RegistryKeyTemplate);
        Assert.Equal("HKLM", definition.Detection.RegistryHive);
        Assert.NotNull(definition.InstallerSettings);
    }

    [Fact]
    public async Task BuildStageManifest_Msp_IsRefused()
    {
        var path = WriteFile("patch.msp");
        var service = MsiService();
        var analysis = await service.AnalyzeAsync(path);

        var ex = Assert.Throws<InvalidOperationException>(() => service.BuildStageManifest(analysis));
        Assert.Contains("msp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analyze_NsisExe_IsPredictedAndNeedsConfirmation()
    {
        var path = WriteFile("setup.exe", "NullsoftInst");
        var analysis = await ExeService().AnalyzeAsync(path);

        Assert.Equal("NSIS", analysis.Engine);
        Assert.Equal("EXE", analysis.InstallerType);
        Assert.Equal(AnalysisConfidence.Predicted, analysis.Confidence);
        Assert.True(analysis.RequiresConfirmation);
        Assert.Equal("/S", analysis.InstallArgs.Value);
        Assert.Equal(AnalysisConfidence.Predicted, analysis.InstallArgs.Confidence);
    }

    [Fact]
    public async Task Analyze_InnoSetupExe_PredictsIs1ArpKey()
    {
        var path = WriteFile("git-setup.exe", "Inno Setup Setup Data");
        var service = ExeService();
        var analysis = await service.AnalyzeAsync(path);

        Assert.Equal("InnoSetup", analysis.Engine);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", analysis.InstallArgs.Value);

        // No PE version resource on the fixture, so the script-defined AppId is
        // genuinely unknown; an omitted key beats an invented one.
        Assert.False(analysis.UninstallRegistryKey.HasValue);

        var manifest = service.BuildStageManifest(analysis,
            new DroppedInstallerOverrides { AppName = "Git", Publisher = "The Git Project", SoftwareVersion = "2.46.0" },
            confirmed: true);

        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Git", manifest.Detection.RegistryKeyRelative);
    }

    [Fact]
    public void PredictUninstallRegistryKey_InnoSetupWithAppName_AppendsIs1()
    {
        var prediction = DroppedInstallerAnalysisService.PredictUninstallRegistryKey(
            "InnoSetup", "Git", "", null, "x64");

        Assert.NotNull(prediction);
        Assert.Equal(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Git_is1", prediction!.Path);
    }

    [Fact]
    public void PredictUninstallRegistryKey_SquirrelWithAppName_IsPerUser()
    {
        var prediction = DroppedInstallerAnalysisService.PredictUninstallRegistryKey(
            "Squirrel", "slack", "", null, "x64");

        Assert.NotNull(prediction);
        Assert.Equal("HKCU", prediction!.Hive);
        Assert.EndsWith(@"Uninstall\slack", prediction.Path);
    }

    [Fact]
    public void PredictUninstallRegistryKey_NsisWithoutDisplayName_ReturnsNull()
    {
        // Some NSIS bootstrappers strip every version resource; nothing in the
        // file reveals the script-defined key name.
        Assert.Null(DroppedInstallerAnalysisService.PredictUninstallRegistryKey("NSIS", "", "", null, "x64"));
    }

    [Fact]
    public async Task BuildStageManifest_PredictedWithoutConfirmation_IsRefused()
    {
        var path = WriteFile("setup.exe", "NullsoftInst");
        var service = ExeService();
        var analysis = await service.AnalyzeAsync(path);

        var overrides = new DroppedInstallerOverrides { AppName = "App", Publisher = "Vendor", SoftwareVersion = "1.0" };

        var ex = Assert.Throws<InvalidOperationException>(() => service.BuildStageManifest(analysis, overrides));
        Assert.Contains("Confirm", ex.Message, StringComparison.OrdinalIgnoreCase);

        var manifest = service.BuildStageManifest(analysis, overrides, confirmed: true);
        Assert.Equal("App", manifest.AppName);
        Assert.Equal("EXE", manifest.InstallerType);
        Assert.Equal("/S", manifest.InstallArgs);
    }

    [Fact]
    public async Task BuildStageManifest_MissingIdentity_IsRefused()
    {
        var path = WriteFile("setup.exe", "NullsoftInst");
        var service = ExeService();
        var analysis = await service.AnalyzeAsync(path);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.BuildStageManifest(analysis, null, confirmed: true));
        Assert.Contains("application name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildStageManifest_UnknownEngineWithoutInstallArgs_IsRefused()
    {
        var path = WriteFile("mystery.exe", "nothing recognizable");
        var service = ExeService();
        var analysis = await service.AnalyzeAsync(path);

        Assert.Equal("Unknown", analysis.Engine);

        var ex = Assert.Throws<InvalidOperationException>(() => service.BuildStageManifest(analysis,
            new DroppedInstallerOverrides { AppName = "Mystery", Publisher = "Vendor", SoftwareVersion = "1.0" },
            confirmed: true));
        Assert.Contains("install arguments", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildStageManifest_BlankPublisher_FallsBackToUnknown()
    {
        var path = WriteFile("setup.exe", "NullsoftInst");
        var service = ExeService();
        var analysis = await service.AnalyzeAsync(path);

        var manifest = service.BuildStageManifest(analysis,
            new DroppedInstallerOverrides { AppName = "App", SoftwareVersion = "1.0" },
            confirmed: true);

        Assert.Equal("Unknown", manifest.Publisher);
    }

    [Fact]
    public async Task Analyze_MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ExeService().AnalyzeAsync(Path.Combine(_root, "absent.exe")));
    }
}
