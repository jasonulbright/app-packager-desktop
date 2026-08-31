using System.IO.Compression;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class InstallerSignatureScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"packr-sigtest-{Guid.NewGuid():N}");

    public InstallerSignatureScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string WriteMarkerExe(string name, params string[] markers)
    {
        var path = Path.Combine(_root, name);
        // Pad ahead of the markers so they never sit at offset 0; real installers
        // carry them well past the PE header.
        var buffer = new List<byte>(Enumerable.Repeat((byte)0x00, 1024));
        foreach (var marker in markers)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes(marker));
            buffer.AddRange(Enumerable.Repeat((byte)0x00, 64));
        }
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }

    private string WriteBytesExe(string name, byte[] leading, byte[] marker)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [.. leading, .. marker, .. Enumerable.Repeat((byte)0x00, 64)]);
        return path;
    }

    private string WriteZip(string name, params string[] entryNames)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entryNames)
            archive.CreateEntry(entry);
        return path;
    }

    [Fact]
    public void Identify_NsisAsciiMarker_ReturnsNsis()
    {
        var path = WriteMarkerExe("nsis-setup.exe", "NullsoftInst");
        Assert.Equal("NSIS", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_NsisFirstHeaderMagic_ReturnsNsis()
    {
        // DEADBEEF little-endian, the NSIS firstheader magic.
        var path = WriteBytesExe("nsis-magic.exe",
            [.. Enumerable.Repeat((byte)0x00, 512)],
            [0xEF, 0xBE, 0xAD, 0xDE]);
        Assert.Equal("NSIS", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_InnoSetupMarker_ReturnsInnoSetup()
    {
        var path = WriteMarkerExe("inno-setup.exe", "Inno Setup Setup Data (6.2.0)");
        Assert.Equal("InnoSetup", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_NsisMarkerAlongsideInnoString_PrefersNsis()
    {
        // Marker precedence matters: an Inno-branded string can appear inside an
        // NSIS payload, but the NSIS engine marker is the definitive one.
        var path = WriteMarkerExe("mixed.exe", "Inno Setup", "NullsoftInst");
        Assert.Equal("NSIS", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_TwoSquirrelMarkers_ReturnsSquirrelNotNsis()
    {
        var path = WriteMarkerExe("squirrel-setup.exe", "SquirrelTemp", "squirrel-install", "NullsoftInst");
        Assert.Equal("Squirrel", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_SingleSquirrelMarkerWithoutUpdateExe_DoesNotReturnSquirrel()
    {
        var path = WriteMarkerExe("weak-squirrel.exe", "squirrel-install", "Inno Setup");
        Assert.Equal("InnoSetup", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_SingleSquirrelMarkerWithUpdateExe_ReturnsSquirrel()
    {
        var path = WriteMarkerExe("squirrel-weak.exe", "squirrel-firstrun", "Update.exe");
        Assert.Equal("Squirrel", InstallerSignatureScanner.Identify(path));
    }

    [Theory]
    [InlineData("WixBundleManifest", "WixBurn")]
    [InlineData("InstallShield Setup Launcher", "InstallShield")]
    [InlineData("Advanced Installer", "AdvancedInstaller")]
    [InlineData("BitRock InstallBuilder", "BitRock")]
    [InlineData("!@InstallEnd@!", "SevenZipSfx")]
    public void Identify_EngineMarkers_ReturnEngine(string marker, string expected)
    {
        var path = WriteMarkerExe($"{expected}.exe", marker);
        Assert.Equal(expected, InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_SevenZipMagic_ReturnsSevenZipSfx()
    {
        var path = WriteBytesExe("sfx.exe",
            [.. Enumerable.Repeat((byte)0x11, 256)],
            [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]);
        Assert.Equal("SevenZipSfx", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_RarMagic_ReturnsWinRarSfx()
    {
        var path = WriteBytesExe("rar-sfx.exe",
            [.. Enumerable.Repeat((byte)0x11, 256)],
            [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]);
        Assert.Equal("WinRarSfx", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_OleCompoundDocumentWithExeExtension_ReturnsNativeMsi()
    {
        var path = WriteBytesExe("wrapped.exe", [], [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        Assert.Equal("NativeMSI", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_MsiExtension_ReturnsNativeMsiWithoutReadingContent()
    {
        var path = Path.Combine(_root, "app.msi");
        File.WriteAllBytes(path, []);
        Assert.Equal("NativeMSI", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_UnrecognizedBinary_ReturnsUnknown()
    {
        var path = WriteMarkerExe("plain.exe", "nothing interesting here");
        Assert.Equal("Unknown", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_PsadtV4Zip_ReturnsPsadtV4()
    {
        var path = WriteZip("psadt4.zip", "Toolkit/PSAppDeployToolkit/PSAppDeployToolkit.psd1", "Toolkit/Deploy-Application.ps1");
        Assert.Equal("PsadtV4", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_PsadtV3Zip_ReturnsPsadtV3()
    {
        var path = WriteZip("psadt3.zip", "Deploy-Application.ps1", "AppDeployToolkit/AppDeployToolkitMain.ps1");
        Assert.Equal("PsadtV3", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_PlainZip_ReturnsZip()
    {
        var path = WriteZip("payload.zip", "app/app.exe");
        Assert.Equal("ZIP", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_MsixPackage_ReturnsMsix()
    {
        var path = WriteZip("app.msix", "AppxManifest.xml", "app.exe");
        Assert.Equal("MSIX", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_MsixBundle_ReturnsMsixBundle()
    {
        var path = WriteZip("app.msixbundle", "AppxMetadata/AppxBundleManifest.xml");
        Assert.Equal("MsixBundle", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_ChocolateyNupkg_ReturnsChocolatey()
    {
        var path = WriteZip("pkg.nupkg", "pkg.nuspec", "tools/chocolateyInstall.ps1");
        Assert.Equal("Chocolatey", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_PlainNupkg_ReturnsNuGet()
    {
        var path = WriteZip("pkg.nupkg", "pkg.nuspec", "lib/net10.0/pkg.dll");
        Assert.Equal("NuGet", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_IntunewinPackage_ReturnsIntunewin()
    {
        var path = WriteZip("app.intunewin", "IntuneWinPackage/Metadata/Detection.xml");
        Assert.Equal("Intunewin", InstallerSignatureScanner.Identify(path));
    }

    [Fact]
    public void Identify_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            InstallerSignatureScanner.Identify(Path.Combine(_root, "absent.exe")));
    }

    [Fact]
    public void ReadPeArchitecture_NonPeFile_ReturnsEmpty()
    {
        var path = WriteMarkerExe("not-pe.exe", "text");
        Assert.Equal("", InstallerSignatureScanner.ReadPeArchitecture(path));
    }

    [Theory]
    [InlineData(0x8664, "x64")]
    [InlineData(0x014C, "x86")]
    [InlineData(0xAA64, "ARM64")]
    public void ReadPeArchitecture_SyntheticPeHeader_ReturnsMachine(int machine, string expected)
    {
        var path = Path.Combine(_root, $"pe-{machine:X}.exe");
        var bytes = new byte[0x100];
        bytes[0] = 0x4D; bytes[1] = 0x5A;                       // MZ
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);        // e_lfanew
        BitConverter.GetBytes(0x00004550).CopyTo(bytes, 0x80);  // PE\0\0
        BitConverter.GetBytes((ushort)machine).CopyTo(bytes, 0x84);
        File.WriteAllBytes(path, bytes);

        Assert.Equal(expected, InstallerSignatureScanner.ReadPeArchitecture(path));
    }
}
