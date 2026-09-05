using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The compiled-script reader against the fixture installers under Fixtures:
/// synthetic headers (stored, deflate, solid deflate, ANSI and Unicode
/// string tables, the electron-builder and MultiUser shapes) and real
/// makensis builds for the LZMA, zlib and bzip2 paths.
/// </summary>
public class NsisScriptReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static NsisMetadata Read(string name) => NsisScriptReader.Read(Fixture(name));

    [Fact]
    public void UncompressedUnicodeHeader_InstallDirUninstallerKeyAndValues()
    {
        var m = Read("nsis-u-none.bin");

        Assert.True(m.HeaderAvailable);
        Assert.Equal("none", m.Compression);
        Assert.True(m.Unicode);
        Assert.Equal(@"$LOCALAPPDATA\TestApp", m.InstallDir);
        Assert.Equal(@"%LOCALAPPDATA%\TestApp", m.InstallDirWindows);
        Assert.Equal(@"$INSTDIR\Uninstall.exe", m.UninstallerPath);
        Assert.Equal(@"""%LOCALAPPDATA%\TestApp\Uninstall.exe"" /S", m.SilentUninstallCommand);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
        Assert.Equal("HKCU", m.RegistryHive);
        Assert.Equal("Test App", m.DisplayName);
        Assert.Equal("1.2.3", m.DisplayVersion);
        Assert.Equal("Test Co", m.Publisher);
        Assert.Equal("Test App", m.Name);
        Assert.Equal("PerUser", m.InstallContext);
        Assert.Equal(@"""$INSTDIR\Uninstall.exe""", m.ArpValues["UninstallString"]);
        Assert.Equal(["CurrentUser"], m.InstallModes);
    }

    [Fact]
    public void AnsiHeader_TwoByteEscapeArguments()
    {
        var m = Read("nsis-a-pf64-hklm-view64.bin");

        Assert.True(m.HeaderAvailable);
        Assert.False(m.Unicode);
        Assert.Equal(@"$PROGRAMFILES64\TestApp", m.InstallDir);
        Assert.Equal(@"%ProgramW6432%\TestApp", m.InstallDirWindows);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
        Assert.Equal("64", m.RegistryView);
        Assert.Equal("PerMachine", m.InstallContext);
        Assert.Equal("AllUsers", m.InstallMode);
    }

    [Fact]
    public void HklmKeyWithoutSetRegView64_RoutesThroughWow6432Node()
    {
        var m = Read("nsis-u-pf-hklm-wow.bin");

        Assert.Equal(@"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
        Assert.Equal("32", m.RegistryView);
        Assert.Equal(@"%ProgramFiles(x86)%\TestApp", m.InstallDirWindows);
        Assert.Contains("WOW6432Node", m.UninstallRegistryKeyNote);
    }

    [Fact]
    public void SetShellVarContextAll_MapsLocalAppDataToProgramData()
    {
        var m = Read("nsis-u-shellvar-all.bin");

        Assert.Equal("all", m.ShellVarContext);
        Assert.Equal(@"%ProgramData%\TestApp", m.InstallDirWindows);
    }

    [Theory]
    [InlineData("nsis-u-deflate.bin", false)]
    [InlineData("nsis-u-deflate-solid.bin", true)]
    public void DeflateHeaderBlocks_Decode(string fixture, bool solid)
    {
        var m = Read(fixture);

        Assert.True(m.HeaderAvailable);
        Assert.Equal("zlib", m.Compression);
        Assert.Equal(solid, m.Solid);
        Assert.Equal(@"$LOCALAPPDATA\TestApp", m.InstallDir);
        Assert.Equal(@"""%LOCALAPPDATA%\TestApp\Uninstall.exe"" /S", m.SilentUninstallCommand);
    }

    [Fact]
    public void UserVariables_ResolveInstDirAndUninstaller()
    {
        var m = Read("nsis-u-indirect.bin");

        Assert.Equal(@"$LOCALAPPDATA\Programs\TestApp", m.InstallDir);
        Assert.Equal(@"$INSTDIR\Uninstall Test App.exe", m.UninstallerPath);
        Assert.Equal(@"""%LOCALAPPDATA%\Programs\TestApp\Uninstall Test App.exe"" /S", m.SilentUninstallCommand);
        Assert.Contains("assigned at run time", m.Note);
        Assert.Equal(["CurrentUser"], m.InstallModes);
        Assert.Equal("CurrentUser", m.InstallMode);
    }

    [Fact]
    public void SwitchableInstaller_OffersTheAllUsersBranch()
    {
        var m = Read("nsis-u-multimode.bin");

        Assert.Equal("CurrentUser", m.InstallMode);
        Assert.Equal(["CurrentUser", "AllUsers"], m.InstallModes);
        Assert.Equal("/allusers", m.AllUsersSwitch);
        Assert.Equal("/currentuser", m.CurrentUserSwitch);
        Assert.Equal("PerUser", m.InstallContext);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
        Assert.Equal(@"""%LOCALAPPDATA%\Programs\TestApp\Uninstall Test App.exe"" /S", m.SilentUninstallCommand);

        var cu = m.ModeVariants["CurrentUser"];
        Assert.Equal("/S", cu.InstallArgs);
        Assert.Equal("PerUser", cu.InstallContext);

        var au = m.ModeVariants["AllUsers"];
        Assert.Equal("/S /allusers", au.InstallArgs);
        Assert.Equal(@"$PROGRAMFILES64\TestApp", au.InstallDir);
        Assert.Equal(@"%ProgramW6432%\TestApp", au.InstallDirWindows);
        Assert.Equal(@"""%ProgramW6432%\TestApp\Uninstall Test App.exe"" /S /allusers", au.SilentUninstallCommand);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", au.UninstallRegistryKey);
        Assert.Equal("HKLM", au.RegistryHive);
        Assert.Equal("64", au.RegistryView);
        Assert.Equal("PerMachine", au.InstallContext);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", au.UninstallRegistryKeyRelative);
    }

    [Fact]
    public void NoModeSwitch_KeepsOneMode()
    {
        var m = Read("nsis-u-single.bin");

        Assert.Equal(["CurrentUser"], m.InstallModes);
        Assert.Single(m.ModeVariants);
        Assert.Equal("", m.AllUsersSwitch);
    }

    [Fact]
    public void MissingFirstHeader_IsReportedNotThrown()
    {
        var m = Read("not-an-installer.bin");

        Assert.False(m.HeaderAvailable);
        Assert.Contains("firstheader", m.Note);
    }

    [Fact]
    public void MarkerInsideTheStub_IsIgnoredWhenNotAligned()
    {
        Assert.False(Read("nsis-marker-in-stub.bin").HeaderAvailable);
    }

    // ── Detection key resolution ────────────────────────────────────

    [Fact]
    public void KeyHeldInAUserVariable_ResolvesToItsLastLiteral()
    {
        var m = Read("nsis-key-var.bin");

        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
        Assert.Equal("Test App", m.DisplayName);
        Assert.Equal("1.2.3", m.DisplayVersion);
    }

    [Fact]
    public void LiteralKeyWithDisplayName_BeatsAVariantKeyBuiltFromAVariable()
    {
        var m = Read("nsis-key-variant.bin");

        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
        Assert.DoesNotContain("computed at run time", m.UninstallRegistryKeyNote);
    }

    [Fact]
    public void Hklm64Root_ForcesThe64BitViewWithoutSetRegView()
    {
        var m = Read("nsis-key-root64.bin");

        Assert.Equal("HKLM", m.RegistryHive);
        Assert.Equal("64", m.RegistryView);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
    }

    [Fact]
    public void SetRegView64AfterTheWriteInCodeOrder_CountsAsOnInit()
    {
        var m = Read("nsis-key-init.bin");

        Assert.Equal("64", m.RegistryView);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
        Assert.Contains("onInit", m.UninstallRegistryKeyNote);
    }

    [Fact]
    public void Shctx_RegistersUnderHklmWhenAllUsersAndProgramFiles()
    {
        var m = Read("nsis-key-shctx-pf.bin");

        Assert.Equal("HKLM", m.RegistryHive);
        Assert.Equal("PerMachine", m.InstallContext);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", m.UninstallRegistryKey);
    }

    // ── makensis builds ─────────────────────────────────────────────

    [Fact]
    public void SolidLzmaUnicodeBuild_Decodes()
    {
        var m = Read("nsis-mk-lzma-solid-unicode.bin");

        Assert.Equal("lzma", m.Compression);
        Assert.True(m.Solid);
        Assert.True(m.Unicode);
        Assert.Equal(@"$LOCALAPPDATA\ProbeApp", m.InstallDir);
        Assert.Equal(@"""%LOCALAPPDATA%\ProbeApp\Uninstall.exe"" /S", m.SilentUninstallCommand);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProbeApp", m.UninstallRegistryKey);
        Assert.Equal("Probe App", m.DisplayName);
        Assert.Equal("asInvoker", m.RequestedExecutionLevel);
    }

    [Fact]
    public void NonSolidLzmaAnsiBuild_Decodes()
    {
        var m = Read("nsis-mk-lzma-ansi.bin");

        Assert.Equal("lzma", m.Compression);
        Assert.False(m.Solid);
        Assert.False(m.Unicode);
        Assert.Equal(@"$LOCALAPPDATA\ProbeApp", m.InstallDir);
        Assert.Equal("Probe App", m.Name);
    }

    [Fact]
    public void SolidZlibBuild_Decodes()
    {
        var m = Read("nsis-mk-zlib-solid.bin");

        Assert.Equal("zlib", m.Compression);
        Assert.True(m.Solid);
        Assert.Equal("1.2.3", m.DisplayVersion);
    }

    [Fact]
    public void Bzip2Build_IsReportedAsNotDecoded()
    {
        var m = Read("nsis-mk-bzip2-solid.bin");

        Assert.Equal("bzip2", m.Compression);
        Assert.False(m.HeaderAvailable);
    }

    // ── Paths and manifests ─────────────────────────────────────────

    [Fact]
    public void WindowsPath_ExpandsInstDirThenFolderConstants()
    {
        Assert.Equal(@"%ProgramW6432%\App\uninstall.exe", NsisScriptReader.ToWindowsPath(@"$INSTDIR\uninstall.exe", @"%ProgramW6432%\App"));
        Assert.Equal(@"%ProgramFiles(x86)%\A", NsisScriptReader.ToWindowsPath(@"$PROGRAMFILES\A"));
        Assert.Equal(@"%ProgramW6432%\A", NsisScriptReader.ToWindowsPath(@"$PROGRAMFILES64\A"));
        Assert.Equal(@"%CommonProgramW6432%\A", NsisScriptReader.ToWindowsPath(@"$COMMONFILES64\A"));
        Assert.Equal(@"%ProgramData%\A", NsisScriptReader.ToWindowsPath(@"$APPDATA\A", allUsersContext: true));
        Assert.Equal(@"%PUBLIC%\Desktop\A", NsisScriptReader.ToWindowsPath(@"$DESKTOP\A", allUsersContext: true));
        Assert.Equal(@"$0\A", NsisScriptReader.ToWindowsPath(@"$0\A"));
    }

    [Fact]
    public void RequestedExecutionLevel_ReadFromRealManifests()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.Equal("asInvoker", PeResources.ReadRequestedExecutionLevel(Path.Combine(system32, "notepad.exe")));
        Assert.Equal("highestAvailable", PeResources.ReadRequestedExecutionLevel(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe")));
        Assert.Equal("", PeResources.ReadRequestedExecutionLevel(Fixture("not-an-installer.bin")));
    }

    [Theory]
    [InlineData(unchecked((int)0x80000002), "HKLM", "")]
    [InlineData(unchecked((int)0xA0000002), "HKLM", "64")]
    [InlineData(unchecked((int)0xC0000002), "HKLM", "32")]
    [InlineData(unchecked((int)0x80000001), "HKCU", "")]
    [InlineData(0, "SHCTX", "")]
    [InlineData(0x20000000, "SHCTX", "64")]
    public void RegistryRoots_SplitIntoHiveAndForcedView(int root, string name, string view)
    {
        Assert.Equal((name, view), NsisScriptReader.ResolveRegistryRoot(root));
    }
}
