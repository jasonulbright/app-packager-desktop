using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class SilentSwitchDatabaseTests
{
    [Theory]
    [InlineData("NativeMSI")]
    [InlineData("NSIS")]
    [InlineData("InnoSetup")]
    [InlineData("InstallShield")]
    [InlineData("WixBurn")]
    [InlineData("AdvancedInstaller")]
    [InlineData("BitRock")]
    [InlineData("Squirrel")]
    [InlineData("MSIX")]
    [InlineData("MsixBundle")]
    [InlineData("PsadtV3")]
    [InlineData("PsadtV4")]
    [InlineData("Chocolatey")]
    [InlineData("NuGet")]
    [InlineData("Intunewin")]
    [InlineData("SevenZipSfx")]
    [InlineData("WinRarSfx")]
    [InlineData("ZIP")]
    [InlineData("MSP")]
    public void Lookup_KnownEngine_HasCommandsAndNotes(string engine)
    {
        Assert.True(SilentSwitchDatabase.IsKnownEngine(engine));

        var entry = SilentSwitchDatabase.Lookup(engine);
        Assert.False(string.IsNullOrWhiteSpace(entry.Install));
        Assert.False(string.IsNullOrWhiteSpace(entry.Uninstall));
        Assert.False(string.IsNullOrWhiteSpace(entry.Notes));
    }

    [Fact]
    public void Lookup_UnrecognizedEngine_FallsBackToUnknown()
    {
        Assert.False(SilentSwitchDatabase.IsKnownEngine("SomeNewInstaller"));

        var entry = SilentSwitchDatabase.Lookup("SomeNewInstaller");
        Assert.Equal(SilentSwitchDatabase.Lookup("Unknown").Notes, entry.Notes);
    }

    [Fact]
    public void Resolve_Msi_SubstitutesFileNameAndProductCode()
    {
        var result = SilentSwitchDatabase.Resolve("NativeMSI", "7z2408-x64.msi", "{23170F69-40C1-2702-2408-000001000000}");

        Assert.Equal("msiexec.exe /i \"7z2408-x64.msi\" /qn /norestart", result.InstallCommand);
        Assert.Equal("msiexec.exe /x \"{23170F69-40C1-2702-2408-000001000000}\" /qn /norestart", result.UninstallCommand);
        Assert.Equal("/qn /norestart", result.InstallArgs);
        Assert.Equal("/qn /norestart", result.UninstallArgs);
    }

    [Fact]
    public void Resolve_Nsis_UsesUppercaseSilentSwitch()
    {
        var result = SilentSwitchDatabase.Resolve("NSIS", "setup.exe");

        Assert.Equal("\"setup.exe\" /S", result.InstallCommand);
        Assert.Equal("/S", result.InstallArgs);
        Assert.Equal("\"uninstall.exe\" /S", result.UninstallCommand);
        Assert.Equal("/S", result.UninstallArgs);
    }

    [Fact]
    public void Resolve_InnoSetup_EmitsFullSilentSet()
    {
        var result = SilentSwitchDatabase.Resolve("InnoSetup", "git-setup.exe");

        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", result.InstallArgs);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", result.UninstallArgs);
    }

    [Fact]
    public void Resolve_WixBurn_UsesUninstallSwitchOnTheBundleItself()
    {
        var result = SilentSwitchDatabase.Resolve("WixBurn", "bundle.exe");

        Assert.Equal("/quiet /norestart", result.InstallArgs);
        Assert.Equal("\"bundle.exe\" /uninstall /quiet /norestart", result.UninstallCommand);
        Assert.Equal("/uninstall /quiet /norestart", result.UninstallArgs);
    }

    [Fact]
    public void Resolve_Squirrel_PointsUninstallAtPerUserUpdateExe()
    {
        var result = SilentSwitchDatabase.Resolve("Squirrel", "SlackSetup.exe", productCode: null, appName: "slack");

        Assert.Equal("\"SlackSetup.exe\" --silent", result.InstallCommand);
        Assert.Equal("--silent", result.InstallArgs);
        Assert.Equal(@"""%LOCALAPPDATA%\slack\Update.exe"" --uninstall -s", result.UninstallCommand);
        Assert.Equal("--uninstall -s", result.UninstallArgs);
    }

    [Fact]
    public void Resolve_BitRock_UsesUnattendedMode()
    {
        var result = SilentSwitchDatabase.Resolve("BitRock", "postgresql.exe");
        Assert.Equal("--mode unattended --unattendedmodeui none", result.InstallArgs);
    }

    [Fact]
    public void Resolve_UnknownEngine_YieldsNoArgumentsButKeepsGuidance()
    {
        var result = SilentSwitchDatabase.Resolve("Unknown", "mystery.exe");

        Assert.Equal("", result.InstallArgs);
        Assert.Equal("", result.UninstallArgs);
        Assert.Contains("/VERYSILENT", result.Notes);
    }

    [Fact]
    public void Resolve_ProseEntry_YieldsNoArguments()
    {
        // Self-extracting archives have no silent switches of their own; the
        // template is guidance, and must not be mistaken for a command line.
        var result = SilentSwitchDatabase.Resolve("SevenZipSfx", "bundle.exe");

        Assert.Equal("", result.InstallArgs);
        Assert.Equal("", result.UninstallArgs);
    }
}
