using System.IO.Compression;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Velopack setup detection and embedded nuspec metadata, exercised against
/// byte-level fixtures built here so no vendor installer is needed.
/// </summary>
public class VelopackAnalysisTests : IDisposable
{
    private const string SampleNuspec =
        "<package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata>"
        + "<id>Sample.Desktop</id><title>Sample Desktop</title><version>26.8.1-build.100</version>"
        + "<authors>Sample Vendor</authors><mainExe>Sample.exe</mainExe>"
        + "<machineArchitecture>x64</machineArchitecture><channel>win</channel></metadata></package>";

    private static readonly byte[] BundleSignature =
    [
        0x94, 0xf0, 0xb1, 0x7b, 0x68, 0x93, 0xe0, 0x29, 0x37, 0xeb, 0x34, 0xef, 0x53, 0xaa, 0xe7, 0xd4,
        0x2b, 0x54, 0xf5, 0x70, 0x7e, 0xf5, 0xd6, 0xf5, 0x78, 0x54, 0x98, 0x3e, 0x5e, 0x94, 0xed, 0x7d
    ];

    private const int OffsetPosition = 256;
    private const int LengthPosition = 264;
    private const int SignaturePosition = 272;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"packr-velopack-{Guid.NewGuid():N}");

    public VelopackAnalysisTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Writes a setup stub carrying the bundle header, the package, and trailing
    /// data standing in for an Authenticode signature.
    /// </summary>
    private string WriteSetup(string name, string? nuspec = SampleNuspec, int stubSize = 8192)
    {
        var path = Path.Combine(_root, name);
        var package = BuildPackage(nuspec);

        var stub = new byte[stubSize];
        stub[0] = 0x4D;
        stub[1] = 0x5A;
        // The Squirrel lifecycle markers are deliberate: a Velopack payload
        // carries them, and the bundle header has to win over them.
        var compatibility = Encoding.ASCII.GetBytes("SquirrelTemp squirrel-install Update.exe");
        Array.Copy(compatibility, 0, stub, 80, compatibility.Length);
        Array.Copy(BitConverter.GetBytes((long)stubSize), 0, stub, OffsetPosition, 8);
        Array.Copy(BitConverter.GetBytes((long)package.Length), 0, stub, LengthPosition, 8);
        Array.Copy(BundleSignature, 0, stub, SignaturePosition, BundleSignature.Length);

        using var stream = File.Create(path);
        stream.Write(stub);
        stream.Write(package);
        stream.Write(new byte[70 * 1024]);
        return path;
    }

    private static byte[] BuildPackage(string? nuspec)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using (var update = new StreamWriter(archive.CreateEntry("Update.exe").Open()))
                update.Write("MZ");
            if (nuspec is not null)
            {
                using var entry = new StreamWriter(archive.CreateEntry("Sample.Desktop.nuspec").Open());
                entry.Write(nuspec);
            }
        }
        return buffer.ToArray();
    }

    private static void Overwrite(string path, int position, long value)
    {
        using var stream = File.OpenWrite(path);
        stream.Position = position;
        stream.Write(BitConverter.GetBytes(value));
    }

    [Fact]
    public void Identify_RenamedSetup_WinsOverSquirrelMarkers()
    {
        Assert.Equal("Velopack", InstallerSignatureScanner.Identify(WriteSetup("renamed.exe")));
    }

    [Fact]
    public void Identify_FileThatOnlyMentionsVelopack_IsNotAnInstaller()
    {
        var path = Path.Combine(_root, "DraftableDesktop.exe");
        File.WriteAllText(path, "MZ Velopack velopack::bundle Update.exe --silent");
        Assert.Equal("Unknown", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Read_PackageBeyondTheScanWindow_ReturnsNuspecFactsAndReleasesTheFile()
    {
        var path = WriteSetup("large-stub.exe", stubSize: InstallerSignatureScanner.ScanWindowBytes + 512);
        var metadata = VelopackMetadataReader.Read(path);

        Assert.True(metadata.HeaderAvailable);
        Assert.Equal("Sample.Desktop", metadata.PackageId);
        Assert.Equal("Sample Desktop", metadata.DisplayName);
        Assert.Equal("26.8.1", metadata.DisplayVersion);
        Assert.Equal("26.8.1-build.100", metadata.PackageVersion);
        Assert.Equal("Sample Vendor", metadata.Publisher);
        Assert.Equal("x64", metadata.Architecture);
        Assert.Equal("Sample.exe", metadata.MainExe);
        Assert.Equal("win", metadata.Channel);
        Assert.Equal(@"%LOCALAPPDATA%\Sample.Desktop", metadata.InstallDir);
        Assert.Equal(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sample.Desktop", metadata.UninstallRegistryKey);
        Assert.Equal(@"""%LOCALAPPDATA%\Sample.Desktop\Update.exe"" --uninstall --silent", metadata.SilentUninstallCommand);

        // Neither the mapping nor the entry stream may keep the file locked.
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Read_NuspecWithoutTitleOrNamespace_FallsBackToThePackageId()
    {
        var path = WriteSetup("minimal.exe",
            "<package><metadata><id>OtherApp</id><version>1.2.3+build.9</version><mainExe>Other.exe</mainExe></metadata></package>");
        var metadata = VelopackMetadataReader.Read(path);

        Assert.True(metadata.HeaderAvailable);
        Assert.Equal("OtherApp", metadata.DisplayName);
        Assert.Equal("1.2.3", metadata.DisplayVersion);
        Assert.Equal("1.2.3+build.9", metadata.PackageVersion);
    }

    [Theory]
    [InlineData("max-offset.exe", OffsetPosition, long.MaxValue)]
    [InlineData("negative-offset.exe", OffsetPosition, -1L)]
    [InlineData("max-length.exe", LengthPosition, long.MaxValue)]
    [InlineData("empty-package.exe", LengthPosition, 0L)]
    public void Read_DamagedHeaderValue_StaysDetectedButInventsNothing(string name, int position, long value)
    {
        var path = WriteSetup(name);
        Overwrite(path, position, value);

        Assert.Equal("Velopack", InstallerSignatureScanner.Identify(path));
        var metadata = VelopackMetadataReader.Read(path);
        Assert.False(metadata.HeaderAvailable);
        Assert.Equal("", metadata.UninstallRegistryKey);
        Assert.Equal("", metadata.SilentUninstallCommand);
        Assert.Contains("outside the file", metadata.Note);
    }

    [Fact]
    public void Read_PackageWithoutANuspec_ReportsTheGapWithoutAnAppId()
    {
        var metadata = VelopackMetadataReader.Read(WriteSetup("no-manifest.exe", nuspec: null));

        Assert.False(metadata.HeaderAvailable);
        Assert.Contains("nuspec", metadata.Note);
        Assert.Equal("", metadata.PackageId);
        Assert.Equal("", metadata.UninstallRegistryKey);
    }

    [Theory]
    [InlineData("<package")]
    [InlineData("<package><metadata><id>../App</id><version>1.2.3</version></metadata></package>")]
    [InlineData("<!DOCTYPE package [<!ENTITY id \"App\">]><package><metadata><id>&id;</id><version>1.2.3</version></metadata></package>")]
    [InlineData("<package><metadata><id>App</id><version>2026.9</version></metadata></package>")]
    public void Read_InvalidManifest_ProducesNoCommandOrKey(string nuspec)
    {
        var metadata = VelopackMetadataReader.Read(WriteSetup("bad-manifest.exe", nuspec));

        Assert.False(metadata.HeaderAvailable);
        Assert.Contains("Velopack metadata unavailable", metadata.Note);
        Assert.Equal("", metadata.UninstallRegistryKey);
        Assert.Equal("", metadata.SilentUninstallCommand);
    }

    [Fact]
    public async Task Analyze_VelopackSetup_ProposesAPerUserPackageFromTheNuspec()
    {
        var path = WriteSetup("renamed setup.exe");
        var service = new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true });

        var analysis = await service.AnalyzeAsync(path);

        Assert.Equal("Velopack", analysis.Engine);
        Assert.Equal("EXE", analysis.InstallerType);
        Assert.Equal("User", analysis.InstallContext);
        Assert.Equal("Sample Desktop", analysis.AppName.Value);
        Assert.Equal("Sample Vendor", analysis.Publisher.Value);
        Assert.Equal("26.8.1", analysis.SoftwareVersion.Value);
        Assert.Equal("26.8.1-build.100", analysis.PackageVersion);
        Assert.Equal("Sample.Desktop", analysis.PackageId);
        // The stub has no PE header of its own; the nuspec names the application.
        Assert.Equal("x64", analysis.Architecture);
        Assert.Equal("--silent", analysis.InstallArgs.Value);
        Assert.Equal(@"""%LOCALAPPDATA%\Sample.Desktop\Update.exe"" --uninstall --silent", analysis.UninstallCommand.Value);
        Assert.Equal(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sample.Desktop", analysis.UninstallRegistryKey.Value);
        Assert.Equal("RegistryKeyValue", analysis.Detection.Type);
        Assert.Equal("CurrentUser", analysis.Detection.Hive);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sample.Desktop", analysis.Detection.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", analysis.Detection.ValueName);
        Assert.Equal("26.8.1", analysis.Detection.ExpectedValue);
    }

    [Fact]
    public async Task Analyze_VelopackSetupWithoutReadableMetadata_ProposesNoKeyOrUninstall()
    {
        var path = WriteSetup("damaged.exe", nuspec: null);
        var service = new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true });

        var analysis = await service.AnalyzeAsync(path);

        Assert.Equal("Velopack", analysis.Engine);
        Assert.Equal("Machine", analysis.InstallContext);
        Assert.False(analysis.UninstallRegistryKey.HasValue);
        Assert.Equal("--silent", analysis.InstallArgs.Value);
        Assert.Equal("", analysis.UninstallCommand.Value);
    }

    [Fact]
    public async Task BuildStageManifest_VelopackSetup_StagesAUserInstall()
    {
        var path = WriteSetup("Sample Desktop Setup.exe");
        var service = new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true });
        var analysis = await service.AnalyzeAsync(path);

        var manifest = service.BuildStageManifest(analysis, confirmed: true);

        Assert.Equal("Sample Desktop", manifest.AppName);
        Assert.Equal("26.8.1", manifest.SoftwareVersion);
        Assert.Equal("User", manifest.InstallContext);
        Assert.Equal("--silent", manifest.InstallArgs);
        Assert.Equal(@"""%LOCALAPPDATA%\Sample.Desktop\Update.exe"" --uninstall --silent", manifest.UninstallCommand);
        Assert.Equal("CurrentUser", manifest.Detection.Hive);
        Assert.Equal("26.8.1", manifest.Detection.ExpectedValue);
    }

    [Fact]
    public void BuildDefinition_VelopackUninstall_ExpandsTheFolderTokenOnTheClient()
    {
        var expression = WrapperGeneratorService.UninstallExpressionFromCommandLine(
            @"""%LOCALAPPDATA%\Sample.Desktop\Update.exe"" --uninstall --silent");

        Assert.Contains(@"[Environment]::ExpandEnvironmentVariables('%LOCALAPPDATA%\Sample.Desktop\Update.exe')", expression);
        Assert.Contains("'--uninstall', '--silent'", expression);
    }

    [Fact]
    public void SwitchTable_Velopack_InstallsSilentlyAndLeavesUninstallToTheMetadata()
    {
        var switches = SilentSwitchDatabase.Resolve("Velopack", "Sample Desktop Setup.exe");

        Assert.Equal("\"Sample Desktop Setup.exe\" --silent", switches.InstallCommand);
        Assert.Equal("--silent", switches.InstallArgs);
        Assert.Equal("", switches.UninstallCommand);
        Assert.Contains("--installto", switches.Notes);
    }
}
