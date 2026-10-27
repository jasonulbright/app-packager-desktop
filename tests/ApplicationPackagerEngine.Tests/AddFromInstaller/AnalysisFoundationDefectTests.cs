using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
using static ApplicationPackagerEngine.Tests.AddFromInstaller.AddFromInstallerTestBed;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>The table defects of the drop flow, the bounded extraction that replaced the unbounded 7-Zip run, and the drop adapter.</summary>
public sealed class AnalysisFoundationDefectTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public void InstallShieldDefaults_UseInstallShieldSwitchesNotBitRockSwitches()
    {
        var fragments = InstallerSettingsGenerator.GenerateDefaults("InstallShield")!.Select(p => p.CommandFragment).ToList();

        Assert.DoesNotContain(fragments, f => f!.Contains("--mode", StringComparison.Ordinal) || f.Contains("--unattendedmodeui", StringComparison.Ordinal));
        Assert.Equal(["/s", "/v\"/qn\""], fragments);
    }

    [Theory]
    [InlineData("NativeMSI", "/qn /norestart")]
    [InlineData("InnoSetup", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER")]
    [InlineData("NSIS", "/S /currentuser")]
    [InlineData("WixBurn", "/quiet /norestart")]
    [InlineData("InstallShield", "/s /v\"/qn\"")]
    public void AlignedSettings_ResolveToTheAnalysedArgumentsWithNoSwitchTwice(string engine, string args)
    {
        var settings = InstallerSettingsGenerator.AlignWithArgs(InstallerSettingsGenerator.GenerateDefaults(engine), args, out var rest);

        var resolved = StagingService.ResolveInstallerArgs(rest, settings, null);

        Assert.Equal(args.Split(' ').Order(), resolved.Split(' ').Order());
    }

    [Fact]
    public void AdvancedInstaller_UsesTheDocumentedBootstrapperSwitch()
    {
        var resolved = SilentSwitchDatabase.Resolve("AdvancedInstaller", "setup.exe");

        Assert.Equal("/exenoui /qn /norestart", resolved.InstallArgs);
        Assert.Equal("/exenoui /x // /qn /norestart", resolved.UninstallArgs);
        Assert.Equal("https://docs.advancedinstaller.com/exe-setup-file.html", resolved.Source);
    }

    [Fact]
    public async Task DropDefinition_CopiesTheExpectedValue()
    {
        var service = new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true });
        var analysis = await service.AnalyzeAsync(_bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe"));

        var edited = service.BuildDefinition(analysis, new DroppedInstallerOverrides { SoftwareVersion = "9.9.9" }, confirmed: true);
        var read = service.BuildDefinition(analysis, confirmed: true);

        Assert.Equal("9.9.9", edited.Detection.ExpectedValue);
        Assert.Equal(StageTokens.HeaderDisplayVersionToken, read.Detection.ExpectedValue);
    }

    [Fact]
    public async Task BoundedExtraction_WritesOnlyIntoTheSandboxAndDeletesItOnDispose()
    {
        var zip = InstallerAnalysisTestFiles.WriteZip(_bed.PathOf("payload.zip"), ("app/tool.exe", [(byte)'M', (byte)'Z']), ("readme.txt", InstallerAnalysisTestFiles.Text("hello")));
        var service = new ArchiveExtractionService("7z-not-used.exe", new AnalysisSandbox(_bed.Sandbox));

        string folder;
        using (var result = await service.ExtractAsync(zip))
        {
            Assert.True(result.Success, result.ErrorMessage);
            folder = result.OutputDirectory!;
            Assert.StartsWith(_bed.Sandbox, folder, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(folder, "app", "tool.exe")));
        }

        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.GetDirectories(_bed.Sandbox));
    }

    [Fact]
    public async Task BoundedExtraction_RefusesAPayloadOverTheLimitAndLeavesNothing()
    {
        var zip = InstallerAnalysisTestFiles.WriteZip(_bed.PathOf("big.zip"), ("big.bin", new byte[256 * 1024]));
        var limits = AnalysisLimits.Default with { MaxExtractBytes = 64 * 1024 };
        var service = new ArchiveExtractionService("7z-not-used.exe", new AnalysisSandbox(_bed.Sandbox), limits);

        using var result = await service.ExtractAsync(zip);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Empty(Directory.GetDirectories(_bed.Sandbox));
    }

    [Fact]
    public async Task BoundedExtraction_StopsOnCancel()
    {
        var zip = InstallerAnalysisTestFiles.WriteZip(_bed.PathOf("payload.zip"), ("a.txt", InstallerAnalysisTestFiles.Text("a")));
        var service = new ArchiveExtractionService("7z-not-used.exe", new AnalysisSandbox(_bed.Sandbox));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExtractAsync(zip, cts.Token));
        Assert.Empty(Directory.GetDirectories(_bed.Sandbox));
    }

    [Fact]
    public async Task DropAdapter_DeepAnalysisRunsThroughTheUnifiedService()
    {
        const string BundleId = "{11112222-3333-4444-5555-666677778888}";
        var path = _bed.Marked("bundle.exe", "WixBundleManifest");
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "WixBurn",
            [Fact("UninstallRegistryKey", @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, "Confirmed", "burn-manifest")]);
        var service = new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true }, fullAnalysis: () => fake);

        var analysis = await service.AnalyzeAsync(path, deepInspect: true);

        Assert.Equal(1, fake.AnalyzeCalls);
        Assert.EndsWith(BundleId, analysis.UninstallRegistryKey.Value);
        Assert.EndsWith(BundleId, analysis.Detection.RegistryKeyRelative);
    }

    [Fact]
    public async Task DropAdapter_RefusesATypeWithNoDefinedFlow()
    {
        var path = _bed.PathOf("payload.zip");
        File.WriteAllText(path, "PK");
        var fake = new FakeInstallerAnalysisService();
        var service = new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true }, fullAnalysis: () => fake);

        await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() => service.AnalyzeAsync(path, deepInspect: true));
        Assert.Equal(0, fake.AnalyzeCalls);
    }

    [Fact]
    public async Task DropStage_RefusesAnMsixAndPointsToTheDefinition()
    {
        var service = new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true });
        var analysis = await service.AnalyzeAsync(_bed.Msix());

        var ex = Assert.Throws<InvalidOperationException>(() => service.BuildStageManifest(analysis, confirmed: true));
        Assert.Contains("Save it as a definition", ex.Message);
    }

    [Fact]
    public void MspWrappers_ApplyThePatchAndRemoveItByItsCodes()
    {
        var folder = Directory.CreateDirectory(_bed.PathOf("msp-wrappers")).FullName;
        const string Uninstall = "$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/package', '{A}', '/uninstall', '{B}', '/qn', '/norestart') -Wait -PassThru -NoNewWindow";

        new WrapperGeneratorService().GenerateMspWrappers(folder, "update.msp", "/qn /norestart REINSTALLMODE=ecmus REINSTALL=ALL", Uninstall);

        var install = File.ReadAllText(Path.Combine(folder, "install.ps1"));
        Assert.Contains("@('/p', \"`\"$mspPath`\"\", '/qn /norestart REINSTALLMODE=ecmus REINSTALL=ALL')", install);
        Assert.Contains(Uninstall, File.ReadAllText(Path.Combine(folder, "uninstall.ps1")));
    }
}
