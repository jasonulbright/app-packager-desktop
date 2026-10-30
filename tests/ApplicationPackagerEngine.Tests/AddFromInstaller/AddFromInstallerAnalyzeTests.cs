using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
using static ApplicationPackagerEngine.Tests.AddFromInstaller.AddFromInstallerTestBed;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>The unified analysis per installer family: evidence on every value, review reasons and the detection proposal.</summary>
public sealed class AddFromInstallerAnalyzeTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    [Theory]
    [InlineData("setup.msi")]
    [InlineData("setup.EXE")]
    [InlineData("patch.msp")]
    [InlineData("app.msix")]
    [InlineData("app.appx")]
    [InlineData("app.msixbundle")]
    [InlineData("app.appxbundle")]
    public void AcceptedTypes_HaveADefinedFlow(string name) =>
        Assert.Null(_bed.Real().RefusalReason(name));

    [Theory]
    [InlineData("payload.zip")]
    [InlineData("install.ps1")]
    [InlineData("tool.intunewin")]
    [InlineData("package.nupkg")]
    [InlineData("noextension")]
    public void OtherTypes_AreRefusedWithAPlainMessage(string name)
    {
        var reason = _bed.Real().RefusalReason(name);
        Assert.NotNull(reason);
        Assert.Contains("Use an .msi, .exe, .msp, .msix, .appx, .msixbundle or .appxbundle file.", reason);
    }

    [Fact]
    public async Task RefusedType_IsRefusedBeforeTheAnalysisRuns()
    {
        var fake = new FakeInstallerAnalysisService();
        var path = _bed.PathOf("payload.zip");
        File.WriteAllText(path, "PK");

        var ex = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() => _bed.Over(fake).AnalyzeAsync(path, NoProgress, CancellationToken.None));

        Assert.Contains(".zip", ex.Message);
        Assert.Equal(0, fake.AnalyzeCalls);
    }

    [Fact]
    public void Folder_IsRefused() =>
        Assert.Equal("The path is a folder. Add from file takes one installer file.", _bed.Real().RefusalReason(_bed.Root));

    [Fact]
    public async Task Cancel_StopsTheAnalysis()
    {
        var path = _bed.Msi();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _bed.Real().AnalyzeAsync(path, NoProgress, cts.Token));
    }

    [Fact]
    public async Task Msi_IsConfirmedWithAProductCodeRule()
    {
        var path = _bed.Msi();

        var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal("MSI", r.InstallerType);
        Assert.Equal(("Contoso Tool", Evidence.Confirmed), (r.Name.Value, r.Name.Evidence));
        Assert.Equal(("4.5.6", Evidence.Confirmed), (r.Version.Value, r.Version.Evidence));
        Assert.Equal(("Contoso Ltd", Evidence.Confirmed), (r.Vendor.Value, r.Vendor.Evidence));
        Assert.Equal("/qn /norestart", r.InstallArguments.Value);
        Assert.Equal(SilentSwitchDatabase.MsiexecSource, r.InstallArguments.Source);
        Assert.Equal(DetectionKind.MsiProductCode, r.Detection.Kind);
        Assert.Equal(Evidence.Confirmed, r.Detection.Evidence);
        Assert.Equal($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}", r.Detection.Values["Key"]);
        Assert.Equal("4.5.6", r.Detection.Values["Expected"]);
        Assert.False(r.NeedsReview, r.ReviewReason);
        Assert.Null(r.ReviewReason);
        Assert.NotNull(r.Deployment);
    }

    [Fact]
    public async Task Nsis_TakesSwitchesKeyAndViewFromTheScript()
    {
        var path = _bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe");

        var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal("NSIS", r.Framework.Value);
        Assert.Equal(("/S", Evidence.Confirmed), (r.InstallArguments.Value, r.InstallArguments.Evidence));
        Assert.Equal(DetectionKind.RegistryVersion, r.Detection.Kind);
        Assert.Equal("false", r.Detection.Values["Is64Bit"]);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", r.Detection.Values["Key"]);
        Assert.Equal(Evidence.Confirmed, r.Detection.Evidence);
        Assert.False(r.NeedsReview, r.ReviewReason);
    }

    [Fact]
    public async Task ADeploymentReadThatFails_NamesItsCause()
    {
        var path = _bed.Marked("locked.exe", null);
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "Unknown", []);

        InstallerAnalysisException ex;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _bed.Over(fake).AnalyzeAsync(path, NoProgress, CancellationToken.None));

        Assert.Contains("The deployment fields could not be read", ex.Message);
        Assert.Contains("locked.exe", ex.Message);
    }

    [Fact]
    public async Task UnknownInstaller_DerivesNoSilentSwitchAndNeedsReview()
    {
        var path = _bed.Marked("mystery.exe", null);

        var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal(Evidence.None, r.Framework.Evidence);
        Assert.Equal(("", Evidence.None), (r.InstallArguments.Value, r.InstallArguments.Evidence));
        Assert.Null(r.InstallArguments.Source);
        Assert.True(r.NeedsReview);
        Assert.Contains(AddFromInstallerService.NoSilentSwitchReason, r.ReviewReason);
        Assert.True(r.Detection.IsDefault);
        Assert.Contains(AddFromInstallerService.DefaultDetectionReason, r.ReviewReason);
    }

    [Theory]
    [InlineData("SevenZipSfx", "!@InstallEnd@!")]
    [InlineData("WinRarSfx", "Rar!\u001a\u0007\u0000")]
    public async Task SelfExtractingArchive_DerivesNoSilentSwitch(string engine, string marker)
    {
        var path = _bed.Marked("sfx.exe", marker);

        var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal(engine, r.Framework.Value);
        Assert.Equal(Evidence.None, r.InstallArguments.Evidence);
        Assert.Contains(AddFromInstallerService.NoSilentSwitchReason, r.ReviewReason);
    }

    [Fact]
    public async Task InstallShield_TableSwitchWithoutASource_NeedsReview()
    {
        var path = _bed.Marked("isetup.exe", "InstallShield");

        var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal("/s /v\"/qn\"", r.InstallArguments.Value);
        Assert.Equal(Evidence.Predicted, r.InstallArguments.Evidence);
        Assert.True(r.NeedsReview);
        Assert.Contains("no documented source", r.ReviewReason);
    }

    [Fact]
    public async Task AdvancedInstaller_UsesTheVendorSwitchWithItsSource()
    {
        var path = _bed.Marked("aisetup.exe", "Advanced Installer");

        var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal("/exenoui /qn /norestart", r.InstallArguments.Value);
        Assert.Equal(Evidence.Predicted, r.InstallArguments.Evidence);
        Assert.Equal("https://docs.advancedinstaller.com/exe-setup-file.html", r.InstallArguments.Source);
        Assert.DoesNotContain("no documented source", r.ReviewReason ?? "");
    }

    [Fact]
    public async Task BurnBundle_PredictsTheBundleKeyNotTheInnerMsiKey()
    {
        const string BundleId = "{11112222-3333-4444-5555-666677778888}";
        var path = _bed.Marked("bundle.exe", "WixBundleManifest");
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "WixBurn",
        [
            Fact("DisplayName", "Contoso Runtime", "Predicted", "pe-version-info"),
            Fact("UninstallRegistryKey", @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, "Confirmed", "burn-manifest"),
            Fact("InstallContext", "PerMachine", "Confirmed", "burn-manifest")
        ], architecture: "x86");

        var r = await _bed.Over(fake).AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, r.Detection.Values["Key"]);
        Assert.Equal("false", r.Detection.Values["Is64Bit"]);
        Assert.Equal("burn-manifest", r.Detection.Source);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, r.Deployment!.Detection.RegistryKeyRelative);
        Assert.Equal("Machine", r.InstallContext.Value);
    }

    [Fact]
    public async Task BitRock_TakesTheProjectKeyUninstallerAndSwitch()
    {
        var path = _bed.Marked("postgresql-13-windows-x64.exe", "BitRock");
        var key = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 13";
        var uninstall = "\"C:\\Program Files\\PostgreSQL\\13\\uninstall-postgresql.exe\" --mode unattended --unattendedmodeui none";
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "BitRock",
            [
                Fact("DisplayName", "PostgreSQL 13", "Confirmed", "bitrock-project"),
                Fact("DisplayVersion", "13.4", "Confirmed", "bitrock-project"),
                Fact("Publisher", "PostgreSQL Global Development Group", "Confirmed", "bitrock-project"),
                Fact("UninstallCommand", uninstall, "Predicted", "bitrock-project"),
                Fact("UninstallRegistryKey", key, "Confirmed", "bitrock-project"),
                Fact("InstallContext", "PerMachine", "Confirmed", "bitrock-project")
            ],
            metadata: [new MetadataBlock(Title: "BitRock InstallBuilder project", Facts: [Fact("Unattended mode", "handled by the project", "Confirmed", "bitrock-project")])],
            modes: [new InstallMode(Name: "PerMachine", Switch: "--mode unattended --unattendedmodeui none", InstallFolder: @"C:\Program Files\PostgreSQL\13", UninstallCommand: uninstall, ArpKey: key, Hive: "HKLM", Context: "PerMachine") { IsDefault = true, View = "64" }]);

        var r = await _bed.Over(fake).AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal(("PostgreSQL 13", Evidence.Confirmed), (r.Name.Value, r.Name.Evidence));
        Assert.Equal(("--mode unattended --unattendedmodeui none", Evidence.Confirmed), (r.InstallArguments.Value, r.InstallArguments.Evidence));
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 13", r.Detection.Values["Key"]);
        Assert.Equal("true", r.Detection.Values["Is64Bit"]);
        Assert.Equal(Evidence.Confirmed, r.Detection.Evidence);
        Assert.Equal(uninstall, r.Deployment!.UninstallCommand.Value);
        Assert.False(r.NeedsReview, r.ReviewReason);
    }

    [Fact]
    public async Task ConventionKey_IsPredictedAndNeedsReview()
    {
        var path = _bed.Marked("squirrel.exe", "SquirrelTemp squirrel-install");
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "Squirrel", [Fact("DisplayName", "Contoso Chat", "Predicted", "pe-version-info")]);

        var r = await _bed.Over(fake).AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.True(r.NeedsReview);
        Assert.Contains("Detection needs review", r.ReviewReason);
    }

    [Fact]
    public async Task Patch_ReadsTheSummaryAndProposesThePatchCodeRule()
    {
        const string PatchCode = "{AAAABBBB-0000-4000-8000-000000000002}";
        var path = _bed.PathOf("contoso-update.msp");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "MSP", [],
            metadata:
            [
                new MetadataBlock(Title: "MSI patch (contoso-update.msp)", Facts:
                [
                    Fact("DisplayName", "Contoso Tool Update (4.5.7)", "Confirmed", "msi-patch-metadata"),
                    Fact("TargetProductName", "Contoso Tool", "Confirmed", "msi-patch-metadata"),
                    Fact("ManufacturerName", "Contoso Ltd", "Confirmed", "msi-patch-metadata"),
                    Fact("Post-patch version", "4.5.7", "Predicted", "msi-patch-metadata"),
                    Fact("Primary PatchCode", PatchCode, "Confirmed", "msi-summary-information"),
                    Fact("Target ProductCodes", ProductCode, "Confirmed", "msi-summary-information")
                ])
            ],
            postPatch: new EffectiveDetection(ProductCode: ProductCode, DisplayName: "Contoso Tool", DisplayVersion: "4.5.7",
                ArpKey: @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductCode, Explanation: "test"));

        var r = await _bed.Over(fake).AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal("MSP", r.InstallerType);
        Assert.NotNull(r.Patch);
        Assert.Equal(PatchCode, r.Patch!.PatchCode);
        Assert.Equal([ProductCode], r.Patch.TargetProductCodes);
        Assert.Contains(r.Patch.BaseProductWarning, r.Notes);
        Assert.Contains(ProductCode, r.Patch.BaseProductWarning);
        Assert.Equal("/qn /norestart REINSTALLMODE=ecmus REINSTALL=ALL", r.InstallArguments.Value);
        Assert.Equal(Evidence.Predicted, r.InstallArguments.Evidence);
        Assert.Equal(SilentSwitchDatabase.MspSource, r.InstallArguments.Source);
        Assert.Equal(DetectionKind.MsiPatchCode, r.Detection.Kind);
        Assert.Equal(PatchCode, r.Detection.Values["PatchCode"]);
        Assert.StartsWith("https://learn.microsoft.com/", r.Detection.Source);
        Assert.Equal($"msiexec.exe /package {ProductCode} /uninstall {PatchCode} /qn /norestart", r.UninstallCommand!.Value);
        Assert.Equal(("4.5.7", Evidence.Predicted), (r.Version.Value, r.Version.Evidence));
    }

    [Fact]
    public async Task UnsignedMsix_IsReadFromTheManifestAndWarnsProminently()
    {
        var path = _bed.Msix();

        var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal("MSIX", r.InstallerType);
        Assert.NotNull(r.Package);
        Assert.Equal("Contoso.Tool", r.Package!.IdentityName);
        Assert.Equal("CN=Contoso Ltd", r.Package.Publisher);
        Assert.Equal("2.3.4.0", r.Package.Version);
        Assert.Equal("x64", r.Package.Architecture);
        Assert.Equal(PackageTrust.Unsigned, r.Package.Trust);
        Assert.StartsWith("The package is not signed.", r.Package.TrustWarning);
        Assert.Single(r.Package.Dependencies);
        Assert.StartsWith("Microsoft.VCLibs.140.00.UWPDesktop", r.Package.Dependencies[0]);
        Assert.Equal(DetectionKind.MsixPackage, r.Detection.Kind);
        Assert.Equal(Evidence.Confirmed, r.Detection.Evidence);
        Assert.True(r.NeedsReview);
        Assert.Contains("not signed", r.ReviewReason);
        Assert.Equal(SilentSwitchDatabase.MsixProvisionSource, r.InstallArguments.Source);
    }

    [Fact]
    public async Task EveryProposedValue_CarriesAnEvidenceLevelAndAPredictedValueASource()
    {
        foreach (var path in new[] { _bed.Msi(), _bed.Fixture("nsis-u-none.bin", "a.exe"), _bed.Marked("ai.exe", "Advanced Installer"), _bed.Msix() })
        {
            var r = await _bed.Real().AnalyzeAsync(path, NoProgress, CancellationToken.None);
            foreach (var (label, evidence, source) in new (string, Evidence, string?)[]
                     {
                         ("framework", r.Framework.Evidence, r.Framework.Source), ("vendor", r.Vendor.Evidence, r.Vendor.Source),
                         ("name", r.Name.Evidence, r.Name.Source), ("version", r.Version.Evidence, r.Version.Source),
                         ("install", r.InstallArguments.Evidence, r.InstallArguments.Source), ("uninstall", r.UninstallArguments.Evidence, r.UninstallArguments.Source),
                         ("context", r.InstallContext.Evidence, r.InstallContext.Source), ("detection", r.Detection.Evidence, r.Detection.Source)
                     })
            {
                Assert.True(Enum.IsDefined(evidence), $"{Path.GetFileName(path)} {label}");
                if (evidence is Evidence.Predicted or Evidence.Confirmed)
                    Assert.False(string.IsNullOrWhiteSpace(source), $"{Path.GetFileName(path)} {label} has evidence {evidence} without a source");
            }
        }
    }
}
