using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>AnalyzeAsync on real files: repository fixtures always, workstation installers when present.</summary>
public sealed class InstallerAnalysisAnalyzeTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public InstallerAnalysisAnalyzeTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox);
    }

    public void Dispose() => _temp.Dispose();

    private Task<AnalysisReport> Analyze(string path, AnalysisLimits? limits = null) =>
        _service.AnalyzeAsync(path, limits ?? AnalysisLimits.Default, NoProgress, CancellationToken.None);

    private static string Value(AnalysisReport r, string name) => r.Deployment.Single(f => f.Name == name).Value;

    [Fact]
    public async Task AnMsiReportsItsFullPropertyTableAndAProductCodeUninstall()
    {
        var r = await Analyze(SevenZipMsi);

        Assert.Equal("NativeMSI", r.Type);
        Assert.Equal("MSI", r.TypeName);
        Assert.Equal("x64", r.Architecture);
        Assert.Equal("7-Zip 24.08 (x64 edition)", Value(r, "DisplayName"));
        Assert.Equal("Confirmed", r.Deployment.Single(f => f.Name == "DisplayName").Confidence);
        Assert.Equal("{23170F69-40C1-2702-2408-000001000000}", Value(r, "ProductCode"));
        Assert.Equal("msiexec.exe /x \"{23170F69-40C1-2702-2408-000001000000}\" /qn /norestart", Value(r, "UninstallCommand"));
        Assert.Equal(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{23170F69-40C1-2702-2408-000001000000}", Value(r, "UninstallRegistryKey"));

        var com = new MsiInspectionService().ReadProperties(SevenZipMsi);
        Assert.Equal(com.ProductName, r.MsiProperties.Single(p => p.Name == "ProductName").Value);
        Assert.Equal(com.ProductVersion, r.MsiProperties.Single(p => p.Name == "ProductVersion").Value);
        Assert.Equal(com.Manufacturer, r.MsiProperties.Single(p => p.Name == "Manufacturer").Value);
        Assert.Equal(64, r.Sha256.Length);
        Assert.Equal("NotSigned", r.Signature!.Status);
        var mode = Assert.Single(r.Modes);
        Assert.Equal("PerMachine", mode.Name);
        Assert.True(mode.IsDefault);
        Assert.Contains(r.Metadata, b => b.Title == "MSI summary information" && b.Facts.Any(f => f.Name == "Template" && f.Value == "x64;1033"));
    }

    [Fact]
    public async Task EveryPropertyTableRowIsListedInTableOrder()
    {
        var path = _temp.PathOf("props.msi");
        using (var b = new TestMsiBuilder(path))
        {
            b.Property("ProductCode", "{AAAAAAAA-1111-2222-3333-444444444444}").Property("ProductName", "Sample").Property("ProductVersion", "1.2.3")
             .Property("Manufacturer", "Contoso").Property("ALLUSERS", "1").Property("ARPNOREPAIR", "1").Property("Custom_Prop", "value with spaces")
             .Template("Intel;1033");
        }

        var r = await Analyze(path);

        Assert.Equal(7, r.MsiProperties.Count);
        Assert.Equal("value with spaces", r.MsiProperties.Single(p => p.Name == "Custom_Prop").Value);
        Assert.Equal("x86", r.Architecture);
        Assert.Equal(@"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{AAAAAAAA-1111-2222-3333-444444444444}", Value(r, "UninstallRegistryKey"));
    }

    [Fact]
    public async Task ADualPurposeMsiOffersBothInstallModes()
    {
        var path = _temp.PathOf("dual.msi");
        using (var b = new TestMsiBuilder(path))
            b.Property("ProductCode", "{BBBBBBBB-1111-2222-3333-444444444444}").Property("ProductName", "Dual").Property("ProductVersion", "2.0")
             .Property("ALLUSERS", "2").Template("x64;1033");

        var r = await Analyze(path);

        Assert.Equal(2, r.Modes.Count);
        Assert.True(r.Modes[0].IsDefault);
        Assert.Equal("PerMachine", r.Modes[0].Name);
        Assert.Equal("PerUser", r.Modes[1].Name);
        Assert.Equal("ALLUSERS=2 MSIINSTALLPERUSER=1", r.Modes[1].Switch);
        Assert.StartsWith(@"HKCU:\", r.Modes[1].ArpKey);
    }

    [Fact]
    public async Task AnNsisInstallerReportsBothBranchesOfItsModeSwitch()
    {
        var r = await Analyze(Fixture("nsis-u-multimode.bin"));

        Assert.Equal("NSIS", r.Type);
        Assert.Equal(2, r.Modes.Count);
        var user = r.Modes.Single(m => m.Name == "PerUser");
        var machine = r.Modes.Single(m => m.Name == "PerMachine");
        Assert.True(user.IsDefault);
        Assert.Equal("", user.Switch);
        Assert.Equal("/allusers", machine.Switch);
        Assert.Equal(@"%ProgramW6432%\TestApp", machine.InstallFolder);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", machine.ArpKey);
        Assert.Equal("64", machine.View);
        Assert.EndsWith("/S /allusers", machine.InstallCommand);
        Assert.Contains(r.Metadata, b => b.Title == "NSIS compiled script");
    }

    [Fact]
    public async Task AnInnoSetupHeaderGivesTheAppIdKeyAndItsModes()
    {
        var r = await Analyze(Fixture("inno-admin-modes-x64.bin"));

        Assert.Equal("InnoSetup", r.Type);
        Assert.Contains(r.Metadata, b => b.Title == "Inno Setup [Setup] header");
        Assert.Equal(2, r.Modes.Count);
        Assert.Contains(r.Modes, m => m.Switch == "/CURRENTUSER" && m.Hive == "HKCU");
        Assert.EndsWith("_is1", Value(r, "UninstallRegistryKey"));
    }

    [Fact]
    public async Task AnEncryptedInnoHeaderIsReportedNotDecoded()
    {
        var r = await Analyze(Fixture("inno-enc-650.bin"));

        Assert.Contains(r.Notes, n => n.Contains("encrypted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ABzip2NsisHeaderIsReportedNotDecoded()
    {
        var r = await Analyze(Fixture("nsis-mk-bzip2-solid.bin"));

        Assert.Contains(r.Notes, n => n.Contains("not decoded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Metadata.Single(b => b.Title == "NSIS compiled script").Facts, f => f.Name == "Header" && f.Value == "not decoded");
    }

    [Fact]
    public async Task AnMsixManifestFillsTheDeploymentFieldsAndTheFamilyName()
    {
        var manifest = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">"
            + "<Identity Name=\"Contoso.Tool\" Publisher=\"CN=Contoso, O=Contoso, C=US\" Version=\"1.2.3.0\" ProcessorArchitecture=\"x64\" />"
            + "<Properties><DisplayName>Contoso Tool</DisplayName><PublisherDisplayName>Contoso</PublisherDisplayName></Properties>"
            + "<Applications><Application Id=\"App\" Executable=\"tool.exe\" /></Applications></Package>";
        var path = WriteZip(_temp.PathOf("tool.msix"), ("AppxManifest.xml", Text(manifest)), ("tool.exe", Text("MZ")));

        var r = await Analyze(path);

        Assert.Equal("MSIX", r.Type);
        Assert.Equal("Contoso Tool", Value(r, "DisplayName"));
        Assert.Equal("1.2.3.0", Value(r, "DisplayVersion"));
        Assert.Equal("Contoso", Value(r, "Publisher"));
        Assert.Equal("x64", r.Architecture);
        var family = r.Metadata.Single(b => b.Title == "MSIX manifest").Facts.Single(f => f.Name == "PackageFamilyName").Value;
        Assert.Equal("Contoso.Tool_" + MsixPackageReader.PublisherHash("CN=Contoso, O=Contoso, C=US"), family);
        Assert.Contains("Get-AppxPackage -Name Contoso.Tool", r.DetectionHint);
    }

    [Fact]
    public async Task AnIntunewinReportsDetectionXmlWithoutKeyMaterial()
    {
        var xml = "<ApplicationInfo xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" ToolVersion=\"1.8.6\"><Name>Contoso</Name>"
            + "<UnencryptedContentSize>1234</UnencryptedContentSize><FileName>IntunePackage.intunewin</FileName><SetupFile>setup.msi</SetupFile>"
            + "<EncryptionInfo><EncryptionKey>S2V5S2V5S2V5</EncryptionKey><MacKey>TWFjTWFjTWFj</MacKey><InitializationVector>SVZJVklW</InitializationVector>"
            + "<Mac>TWFjVmFsdWU=</Mac><ProfileIdentifier>ProfileVersion1</ProfileIdentifier><FileDigest>RGlnZXN0</FileDigest><FileDigestAlgorithm>SHA256</FileDigestAlgorithm></EncryptionInfo>"
            + "<MsiInfo><MsiProductCode>{CCCCCCCC-1111-2222-3333-444444444444}</MsiProductCode><MsiProductVersion>3.1</MsiProductVersion><MsiPublisher>Contoso</MsiPublisher></MsiInfo></ApplicationInfo>";
        var path = WriteZip(_temp.PathOf("app.intunewin"), ("IntuneWinPackage/Metadata/Detection.xml", Text(xml)),
            ("IntuneWinPackage/Contents/IntunePackage.intunewin", new byte[64]));

        var r = await Analyze(path);

        Assert.Equal("Intunewin", r.Type);
        Assert.Equal("{CCCCCCCC-1111-2222-3333-444444444444}", Value(r, "ProductCode"));
        var all = string.Join("\n", r.Metadata.SelectMany(b => b.Facts).Select(f => f.Name + "=" + f.Value).Concat(r.Deployment.Select(f => f.Value)).Concat(r.Notes));
        foreach (var secret in new[] { "S2V5S2V5S2V5", "TWFjTWFjTWFj", "SVZJVklW", "TWFjVmFsdWU=" })
            Assert.DoesNotContain(secret, all);
        Assert.Contains("key material is not shown", all);
    }

    [Fact]
    public async Task ANuGetPackageReadsItsNuspec()
    {
        var nuspec = "<package><metadata><id>Contoso.Lib</id><version>4.5.6</version><authors>Contoso</authors><description>Library</description></metadata></package>";
        var path = WriteZip(_temp.PathOf("contoso.lib.4.5.6.nupkg"), ("Contoso.Lib.nuspec", Text(nuspec)), ("lib/net8.0/a.dll", Text("MZ")));

        var r = await Analyze(path);

        Assert.Equal("NuGet", r.Type);
        Assert.Equal("Contoso.Lib", Value(r, "ProductCode"));
        Assert.Equal("4.5.6", Value(r, "DisplayVersion"));
        Assert.Equal("nuget install Contoso.Lib -Version 4.5.6 -Source \"<SourceDirOrFeed>\"", Value(r, "InstallCommand"));
    }

    [Fact]
    public async Task APsadtV4PackageReadsTheSessionValues()
    {
        var script = "$adtSession = @{\r\n    AppVendor = 'Contoso'\r\n    AppName = 'Contoso Tool'\r\n    AppVersion = '7.0'\r\n    AppArch = 'x64'\r\n}\r\n";
        var path = WriteZip(_temp.PathOf("psadt.zip"), ("Invoke-AppDeployToolkit.ps1", Text(script)),
            ("PSAppDeployToolkit/PSAppDeployToolkit.psd1", Text("@{ ModuleVersion = '4.1.5' }")));

        var r = await Analyze(path);

        Assert.Equal("PsadtV4", r.Type);
        Assert.Equal("Contoso Tool", Value(r, "DisplayName"));
        Assert.Equal("7.0", Value(r, "DisplayVersion"));
        Assert.Equal("4.1.5", r.Metadata.Single(b => b.Title == "PSAppDeployToolkit").Facts.Single(f => f.Name == "Toolkit version").Value);
    }

    [Fact]
    public async Task AFolderAnEmptyFileAndAMissingFileGivePlainMessages()
    {
        var empty = _temp.PathOf("empty.exe");
        File.WriteAllBytes(empty, []);

        Assert.Equal("The path is a folder; open one installer file.", (await Assert.ThrowsAsync<InstallerAnalysisException>(() => Analyze(_temp.Root))).Message);
        Assert.Equal("The file is empty.", (await Assert.ThrowsAsync<InstallerAnalysisException>(() => Analyze(empty))).Message);
        Assert.Equal("The file does not exist.", (await Assert.ThrowsAsync<InstallerAnalysisException>(() => Analyze(_temp.PathOf("none.exe")))).Message);
    }

    [Fact]
    public async Task AFileOverTheSizeLimitIsRefusedBeforeAnyRead()
    {
        var limits = AnalysisLimits.Default with { MaxFileBytes = 1000 };

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => Analyze(SevenZipMsi, limits));

        Assert.StartsWith("The file is larger than the analysis limit", ex.Message);
    }

    [Fact]
    public async Task ProgressNamesEachStep()
    {
        var steps = new List<string>();
        await _service.AnalyzeAsync(SevenZipMsi, AnalysisLimits.Default, new SyncProgress(steps.Add), CancellationToken.None);

        Assert.Contains("Computing the SHA-256 hash", steps);
        Assert.Contains("Checking the signature", steps);
        Assert.Contains("Reading the MSI tables", steps);
    }

    [Fact]
    public async Task ACancelBeforeTheRunEndsItAsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.AnalyzeAsync(SevenZipMsi, AnalysisLimits.Default, NoProgress, cts.Token));
    }

    [Fact]
    public async Task TheFileIsNotHeldOpenAfterTheAnalysis()
    {
        var copy = _temp.PathOf("copy.msi");
        File.Copy(SevenZipMsi, copy);

        await Analyze(copy);
        File.Delete(copy);

        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task WorkstationInstallersMatchTheirKnownValues()
    {
        var ran = 0;
        if (Corpus.Has(Corpus.BurnVcRedist))
        {
            var r = await Analyze(Corpus.BurnVcRedist);
            Assert.Equal(@"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{CA67548A-5EBE-413A-B50C-4B9CEB6D66C6}", Value(r, "UninstallRegistryKey"));
            ran++;
        }
        if (Corpus.Has(Corpus.MspAdobe))
        {
            var r = await Analyze(Corpus.MspAdobe);
            Assert.Equal("26.002.21931", r.PostPatch!.DisplayVersion);
            Assert.Equal("Adobe Acrobat Reader", r.PostPatch.DisplayName);
            ran++;
        }
        if (Corpus.Has(Corpus.SfxAdobe))
        {
            var r = await Analyze(Corpus.SfxAdobe);
            Assert.Equal("{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", r.PostPatch!.ProductCode);
            Assert.Equal("26.002.21931", r.PostPatch.DisplayVersion);
            Assert.Equal("15.007.20033", r.PostPatch.BaseVersion);
            Assert.Equal(@"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", r.PostPatch.ArpKey);
            Assert.Empty(Directory.EnumerateDirectories(_sandbox.Root));
            ran++;
        }
        if (Corpus.Has(Corpus.InnoModes))
        {
            var r = await Analyze(Corpus.InnoModes);
            Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{3C01F274-867C-4D1D-BE8C-CB488C31B0C9}_is1", Value(r, "UninstallRegistryKey"));
            ran++;
        }
        var fixture = await Analyze(Fixture("nsis-u-pf-hklm-wow.bin"));
        Assert.Contains("WOW6432Node", Value(fixture, "UninstallRegistryKey"));
        ran++;
        Assert.True(ran >= 1);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
