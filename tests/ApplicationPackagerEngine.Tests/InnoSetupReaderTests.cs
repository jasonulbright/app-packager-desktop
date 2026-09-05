using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The compiled [Setup] header reader against fixture installers laid out per
/// data version, from the 5.5.7 flag-byte era through 7.0.0.3.
/// </summary>
public class InnoSetupReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static InnoSetupMetadata Read(string name) => InnoSetupReader.Read(Fixture(name));

    [Fact]
    public void PerMachineX64Setup_KeyFolderUninstallerContext()
    {
        var m = Read("inno-admin-670.bin");

        Assert.True(m.HeaderAvailable);
        Assert.Equal("6.7.0 (Unicode)", m.DataVersion);
        Assert.Equal("Test App", m.AppId);
        Assert.Equal("Test App", m.DisplayName);
        Assert.Equal("1.2.3", m.DisplayVersion);
        Assert.Equal("Test Co", m.Publisher);
        Assert.Equal("admin", m.PrivilegesRequired);
        Assert.True(m.Is64BitInstallMode);
        Assert.Equal("PerMachine", m.InstallContext);
        Assert.Equal("HKLM", m.RegistryHive);
        Assert.Equal("64", m.RegistryView);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Test App_is1", m.UninstallRegistryKey);
        Assert.Equal(@"%ProgramFiles%\Test App", m.InstallDirWindows);
        Assert.Equal(@"%ProgramFiles%\Test App\unins000.exe", m.UninstallerPathWindows);
        Assert.Equal(@"""%ProgramFiles%\Test App\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", m.SilentUninstallCommand);
        Assert.Equal("Test App version 1.2.3", m.ArpValues["DisplayName"]);
        Assert.Equal("6.1.0", m.MinWindowsVersion);
        Assert.Equal(["AllUsers"], m.InstallModes);
    }

    [Fact]
    public void PrivilegesLowest_MapsToHkcuAndTheUserProgramsFolder()
    {
        var m = Read("inno-lowest-670.bin");

        Assert.Equal("lowest", m.PrivilegesRequired);
        Assert.Equal(["commandline", "dialog"], m.PrivilegesRequiredOverridesAllowed);
        Assert.Equal("PerUser", m.InstallContext);
        Assert.Equal("HKCU", m.RegistryHive);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", m.UninstallRegistryKey);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Code", m.InstallDirWindows);
        Assert.Contains("/ALLUSERS", m.UninstallRegistryKeyNote);
    }

    [Fact]
    public void CommandLineOverride_OffersTheOtherBranchAsAMode()
    {
        var m = Read("inno-lowest-modes.bin");

        Assert.Equal("CurrentUser", m.InstallMode);
        Assert.Equal(["CurrentUser", "AllUsers"], m.InstallModes);
        Assert.Equal("/ALLUSERS", m.AllUsersSwitch);
        var cu = m.ModeVariants["CurrentUser"];
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", cu.InstallArgs);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Tree_is1", cu.UninstallRegistryKey);
        var au = m.ModeVariants["AllUsers"];
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS", au.InstallArgs);
        Assert.Equal(@"%ProgramFiles%\Tree", au.InstallDirWindows);
        Assert.Equal(@"""%ProgramFiles%\Tree\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", au.SilentUninstallCommand);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Tree_is1", au.UninstallRegistryKey);
        Assert.Equal("64", au.RegistryView);
        Assert.Equal("PerMachine", au.InstallContext);

        var m2 = Read("inno-admin-modes.bin");
        Assert.Equal("AllUsers", m2.InstallMode);
        Assert.Equal(["CurrentUser", "AllUsers"], m2.InstallModes);
        Assert.Equal(@"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1", m2.ModeVariants["AllUsers"].UninstallRegistryKey);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER", m2.ModeVariants["CurrentUser"].InstallArgs);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\WinSCP", m2.ModeVariants["CurrentUser"].InstallDirWindows);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1", m2.ModeVariants["CurrentUser"].UninstallRegistryKey);
    }

    [Fact]
    public void DialogOverrideOnly_KeepsOneMode()
    {
        var m = Read("inno-dialog-only.bin");

        Assert.Equal(["AllUsers"], m.InstallModes);
        Assert.Equal("", m.AllUsersSwitch);
    }

    [Fact]
    public void SetupWithout64BitMode_RoutesToWow6432NodeAnd32BitProgramFiles()
    {
        var m = Read("inno-x86-6401.bin");

        Assert.True(m.HeaderAvailable);
        Assert.Equal("6.4.0.1 (Unicode)", m.DataVersion);
        Assert.False(m.Is64BitInstallMode);
        Assert.Equal("32", m.RegistryView);
        Assert.Equal(@"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1", m.UninstallRegistryKey);
        Assert.Equal(@"%ProgramFiles(x86)%\WinSCP", m.InstallDirWindows);
        Assert.Contains("WOW6432Node", m.UninstallRegistryKeyNote);
    }

    [Fact]
    public void Layout650_ReadsBehindTheEncryptionHeader()
    {
        var m = Read("inno-pu-650.bin");

        Assert.True(m.HeaderAvailable);
        Assert.Equal("poweruser", m.PrivilegesRequired);
        Assert.Equal("PerMachine", m.InstallContext);
        Assert.Equal("HKLM", m.RegistryHive);
    }

    [Fact]
    public void EncryptedHeader_StopsWithANote()
    {
        var m = Read("inno-enc-650.bin");

        Assert.False(m.HeaderAvailable);
        Assert.Equal("6.5.0 (Unicode)", m.DataVersion);
        Assert.Contains("encrypted", m.Note);
    }

    [Theory]
    [InlineData("inno-admin-600.bin", "6.0.0 (ANSI)")]
    [InlineData("inno-admin-622.bin", "6.2.2 (ANSI)")]
    [InlineData("inno-admin-633.bin", "6.3.3 (Unicode)")]
    [InlineData("inno-admin-643.bin", "6.4.3 (Unicode)")]
    [InlineData("inno-admin-654.bin", "6.5.4 (Unicode)")]
    [InlineData("inno-admin-660.bin", "6.6.0 (Unicode)")]
    [InlineData("inno-admin-661.bin", "6.6.1 (Unicode)")]
    [InlineData("inno-admin-7003.bin", "7.0.0.3 (Unicode)")]
    public void EveryLayoutFrom600_DecodesToTheSameAdminSetup(string fixture, string dataVersion)
    {
        var m = Read(fixture);

        Assert.True(m.HeaderAvailable);
        Assert.Equal(dataVersion, m.DataVersion);
        Assert.Equal("admin", m.PrivilegesRequired);
        Assert.Equal("64", m.RegistryView);
        Assert.Equal("1.2.3", m.DisplayVersion);
        Assert.StartsWith(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\", m.UninstallRegistryKey);
    }

    [Fact]
    public void Unicode557_ReadsTheArchitectureFlagByteAndAnEscapedGuid()
    {
        var m = Read("inno-u-557.bin");

        Assert.True(m.HeaderAvailable);
        Assert.Equal("5.5.7 (Unicode)", m.DataVersion);
        Assert.Equal("x64", m.ArchitecturesInstallIn64BitMode);
        Assert.True(m.Is64BitInstallMode);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{37771A20-7167-44C0-B322-FD3E54C56156}_is1", m.UninstallRegistryKey);
        Assert.Equal("admin", m.PrivilegesRequired);
    }

    [Fact]
    public void Ansi557_ReadsPastTheLeadByteTable()
    {
        var m = Read("inno-a-557.bin");

        Assert.True(m.HeaderAvailable);
        Assert.Equal("5.5.7 (ANSI)", m.DataVersion);
        Assert.False(m.Is64BitInstallMode);
        Assert.Equal(@"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AnsiApp_is1", m.UninstallRegistryKey);
        Assert.Equal("admin", m.PrivilegesRequired);
    }

    [Fact]
    public void RunTimeDefaultDirName_StaysUnresolvedButNamesTheKey()
    {
        var m = Read("inno-code-610.bin");

        Assert.True(m.HeaderAvailable);
        Assert.Equal("none", m.PrivilegesRequired);
        Assert.Equal(@"{code:UserPF}\R\R-4.6.0", m.InstallDirWindows);
        Assert.Equal("", m.UninstallerPathWindows);
        Assert.Equal("", m.SilentUninstallCommand);
        Assert.Contains("not resolvable", m.Note);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\R for Windows 4.6.0_is1", m.UninstallRegistryKey);
    }

    [Fact]
    public void ArpDisplayName_PrefersUninstallDisplayNameThenAppVerName()
    {
        Assert.Equal("Test App (User)", Read("inno-names.bin").ArpDisplayName);
        Assert.Equal("Test App 1.2.3", Read("inno-names2.bin").ArpDisplayName);
    }

    [Fact]
    public void UninstallFilesDir_PlacesTheUninstaller()
    {
        Assert.Equal(@"%ProgramFiles%\Test App\uninst\unins000.exe", Read("inno-ufd.bin").UninstallerPathWindows);
    }

    [Fact]
    public void UninstallableNo_ReportsNoKeyAndNoCommand()
    {
        var m = Read("inno-no-unins.bin");

        Assert.Equal("", m.UninstallRegistryKey);
        Assert.Equal("", m.SilentUninstallCommand);
        Assert.Contains("Uninstallable=no", m.UninstallRegistryKeyNote);
    }

    [Fact]
    public void BlockChecksumMismatch_IsReportedNotGuessed()
    {
        var m = Read("inno-badcrc.bin");

        Assert.False(m.HeaderAvailable);
        Assert.Contains("checksum", m.Note);
    }

    [Fact]
    public void FileWithoutALoaderTable_IsUnavailable()
    {
        var m = Read("not-an-installer.bin");

        Assert.False(m.HeaderAvailable);
        Assert.Contains("offset table", m.Note);
    }

    [Fact]
    public void WindowsPath_FollowsTheInstallModeAndBitness()
    {
        Assert.Equal(@"%ProgramFiles%\App", InnoSetupReader.ToWindowsPath(@"{autopf}\App"));
        Assert.Equal(@"%ProgramFiles(x86)%\App", InnoSetupReader.ToWindowsPath(@"{autopf}\App", is64BitMode: false));
        Assert.Equal(@"%LOCALAPPDATA%\Programs\App", InnoSetupReader.ToWindowsPath(@"{autopf}\App", perUser: true));
        Assert.Equal(@"%ProgramFiles%\App\unins000.exe", InnoSetupReader.ToWindowsPath(@"{app}\unins000.exe", appDir: @"%ProgramFiles%\App"));
        Assert.Equal(@"{code:UserPF}\R", InnoSetupReader.ToWindowsPath(@"{code:UserPF}\R"));
        Assert.Equal(@"%ProgramData%\App", InnoSetupReader.ToWindowsPath(@"{commonappdata}\App"));
        Assert.Equal(@"%MYVAR%\App", InnoSetupReader.ToWindowsPath(@"{%MYVAR|default}\App"));
        Assert.Equal(@"{literal}\App", InnoSetupReader.ToWindowsPath(@"{{literal}\App"));
    }
}
