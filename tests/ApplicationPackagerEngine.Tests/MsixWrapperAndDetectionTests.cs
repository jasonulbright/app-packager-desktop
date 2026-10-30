using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class MsixWrapperAndDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msix-wrapper-tests-" + Guid.NewGuid().ToString("N"));

    public MsixWrapperAndDetectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        TestCleanup.DeleteDirectory(_dir);
    }

    private (string Install, string Uninstall) Generate(
        string? thumbprint = "AABBCCDD", bool provisioned = true, string fileName = "install-x64.msix")
    {
        new WrapperGeneratorService().GenerateMsixWrappers(_dir, fileName, thumbprint, provisioned);
        return (File.ReadAllText(Path.Combine(_dir, "install.ps1")),
                File.ReadAllText(Path.Combine(_dir, "uninstall.ps1")));
    }

    [Fact]
    public void ProvisionedInstall_AddsProvisionedPackageWithSkipLicense()
    {
        var (install, _) = Generate();

        Assert.Contains("Add-AppxProvisionedPackage -Online -PackagePath $msixPath -SkipLicense", install);
        Assert.Contains("$msixPath = Join-Path $PSScriptRoot 'install-x64.msix'", install);
        Assert.DoesNotContain("Add-AppxPackage -Path", install);
    }

    [Fact]
    public void BothWrappers_LoadTheCompressionAssemblies()
    {
        var (install, uninstall) = Generate();

        const string addType = "Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem";
        Assert.Contains(addType, install);
        Assert.Contains(addType, uninstall);
    }

    [Fact]
    public void Install_PinsTheSignerThumbprintAndFailsOnMismatch()
    {
        var (install, _) = Generate(thumbprint: "0AF92F9129811F19D5662619BD9E5511ECE9B461");

        Assert.Contains("$expected = '0AF92F9129811F19D5662619BD9E5511ECE9B461'", install);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $msixPath", install);
        Assert.Contains("Exit-Wrapper 2", install); // signature status not Valid
        Assert.Contains("Exit-Wrapper 3", install); // signed by an unexpected certificate
    }

    [Fact]
    public void Install_WithoutThumbprint_OmitsTheSignatureCheck()
    {
        var (install, _) = Generate(thumbprint: null);

        Assert.DoesNotContain("Get-AuthenticodeSignature", install);
        Assert.Contains("Add-AppxProvisionedPackage", install);
    }

    [Fact]
    public void Install_MissingPackage_ExitsOne()
    {
        var (install, _) = Generate();

        Assert.Contains("if (-not (Test-Path -LiteralPath $msixPath)) { Write-Error \"MSIX not found: $msixPath\"; Exit-Wrapper 1 }", install);
    }

    [Fact]
    public void Uninstall_MissingPackage_ExitsZero()
    {
        var (_, uninstall) = Generate();

        Assert.Contains("if (-not (Test-Path -LiteralPath $msixPath)) { Exit-Wrapper 0 }", uninstall);
    }

    [Fact]
    public void ProvisionedUninstall_RemovesProvisioningAndEveryUserRegistration()
    {
        var (_, uninstall) = Generate();

        Assert.Contains("$entry = $zip.GetEntry('AppxManifest.xml')", uninstall);
        Assert.Contains("$identity = $manifest.DocumentElement.Identity", uninstall);
        Assert.Contains("Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName", uninstall);
        Assert.Contains("Get-AppxPackage -AllUsers -Name $identity.Name -PackageTypeFilter $packageType | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers }", uninstall);
        Assert.Contains("Exit-Wrapper 4", uninstall); // manifest missing from the container
    }

    [Fact]
    public void PerUserMode_UsesAddAppxPackageAndPerUserRemoval()
    {
        var (install, uninstall) = Generate(provisioned: false);

        Assert.Contains("Add-AppxPackage -Path $msixPath", install);
        Assert.DoesNotContain("Add-AppxProvisionedPackage", install);
        Assert.Contains("Get-AppxPackage -Name $identity.Name -PackageTypeFilter $packageType | Remove-AppxPackage", uninstall);
        Assert.DoesNotContain("Remove-AppxProvisionedPackage", uninstall);
    }

    [Fact]
    public void ApostropheInFileName_IsEscapedInTheLiteral()
    {
        var (install, _) = Generate(fileName: "jan's-app.msix");

        Assert.Contains("Join-Path $PSScriptRoot 'jan''s-app.msix'", install);
    }

    [Fact]
    public void AllFourWrapperFilesAreWritten()
    {
        Generate();

        foreach (var name in new[] { "install.ps1", "uninstall.ps1", "install.bat", "uninstall.bat" })
            Assert.True(File.Exists(Path.Combine(_dir, name)), name);
    }

    private static StageMsixPackage Package() => new()
    {
        Name = "ohmyposh.cli",
        Version = "31.1.1.0",
        Publisher = "CN=Vendor",
        ProcessorArchitecture = "x64",
        SignerThumbprint = "AABBCC"
    };

    [Fact]
    public void ScriptDetection_QueriesProvisioningStoreByDisplayNameAndVersion()
    {
        var detection = StagingService.BuildMsixScriptDetection(Package());

        Assert.Equal("Script", detection.Type);
        Assert.Equal("PowerShell", detection.ScriptLanguage);
        Assert.Contains("$wanted = [version]'31.1.1.0'", detection.ScriptText);
        Assert.Contains("Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue", detection.ScriptText);
        Assert.Contains("$_.DisplayName -eq 'ohmyposh.cli'", detection.ScriptText);
        Assert.Contains("if ($found -ge $wanted) { Write-Output 'Installed'; exit 0 }", detection.ScriptText);
        Assert.True(detection.Is64Bit);
    }

    [Fact]
    public void ScriptDetection_ForX86Package_IsNot64Bit()
    {
        var package = Package();
        package.ProcessorArchitecture = "x86";

        Assert.False(StagingService.BuildMsixScriptDetection(package).Is64Bit);
    }

    [Fact]
    public void ScriptDetection_EscapesAnApostropheInTheIdentityName()
    {
        var package = Package();
        package.Name = "jan's.app";

        Assert.Contains("$_.DisplayName -eq 'jan''s.app'",
            StagingService.BuildMsixScriptDetection(package).ScriptText);
    }
}
