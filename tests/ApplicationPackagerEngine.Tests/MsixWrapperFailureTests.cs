using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// An AppX cmdlet reports most failures as a non-terminating error. The staged MSIX
/// wrappers ended with exit code 0 after such a failure, so a deployment reported
/// success for a package that was never installed or removed (PowerShell commit
/// c97d69f). Each cmdlet is a Windows PowerShell 5.1 function of the same name, so
/// nothing on this computer changes.
/// </summary>
public sealed class MsixWrapperFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msix-failure-tests-" + Guid.NewGuid().ToString("N"));

    public MsixWrapperFailureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestCleanup.DeleteDirectory(_dir);

    private const string Publisher = "CN=Contoso";
    private static readonly string PublisherId = MsixPackageReader.PublisherHash(Publisher);

    /// <summary>
    /// Stand-ins that raise a non-terminating error when <c>$failing</c> names them, as the real cmdlets do.
    /// </summary>
    private static string Stubs() => $$"""
        function Add-AppxProvisionedPackage { [CmdletBinding()] param([switch]$Online, [string]$PackagePath, [switch]$SkipLicense)
            if ($failing -contains 'Add-AppxProvisionedPackage') { Write-Error 'provisioning failed' } }
        function Add-AppxPackage { [CmdletBinding()] param([string]$Path)
            if ($failing -contains 'Add-AppxPackage') { Write-Error 'registration failed' } }
        function Get-AppxProvisionedPackage { [CmdletBinding()] param([switch]$Online)
            if ($failing -contains 'Get-AppxProvisionedPackage') { Write-Error 'lookup failed'; return }
            [pscustomobject]@{ DisplayName = 'Contoso.Tool'; PackageName = 'Contoso.Tool_1.0.0.0_neutral_~_{{PublisherId}}' } }
        function Remove-AppxProvisionedPackage { [CmdletBinding()] param([switch]$Online, [string]$PackageName)
            if ($failing -contains 'Remove-AppxProvisionedPackage') { Write-Error 'deprovisioning failed' } }
        function Get-AppxPackage { [CmdletBinding()] param([switch]$AllUsers, [string]$Name, [string[]]$PackageTypeFilter)
            if ($failing -contains 'Get-AppxPackage') { Write-Error 'package lookup failed'; return }
            [pscustomobject]@{ PackageFullName = 'Contoso.Tool_1.0.0.0_x64__{{PublisherId}}'; PublisherId = '{{PublisherId}}' } }
        function Remove-AppxPackage { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject, [string]$Package, [switch]$AllUsers)
            process { if ($failing -contains 'Remove-AppxPackage') { Write-Error 'removal failed' } } }
        """;

    private async Task<int> RunAsync(string script, string[] failing, string wrapper)
    {
        var list = failing.Length == 0 ? "@()" : "@(" + string.Join(",", failing.Select(f => $"'{f}'")) + ")";
        var full = $"$failing = {list}\r\n{Stubs()}\r\n& '{Path.Combine(_dir, wrapper)}'\r\nexit $LASTEXITCODE";
        var (exitCode, _, _) = await WindowsPowerShellHost.RunAsync(full, null, CancellationToken.None, "apd-msixfailure");
        return exitCode;
    }

    private string Generate(bool provisioned)
    {
        var package = MsixFixture.WritePackage(_dir, "contoso.msix", MsixFixture.AppxManifest(name: "Contoso.Tool", publisher: Publisher));
        new WrapperGeneratorService().GenerateMsixWrappers(_dir, Path.GetFileName(package), null, provisioned, PublisherId);
        return package;
    }

    [Theory]
    [InlineData(true, "Add-AppxProvisionedPackage")]
    [InlineData(false, "Add-AppxPackage")]
    public async Task AnInstallThatTheCmdletRefusesExitsWithAFailure(bool provisioned, string cmdlet)
    {
        Generate(provisioned);
        Assert.Equal(1, await RunAsync("", [cmdlet], "install.ps1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnInstallThatSucceedsExitsWithZero(bool provisioned)
    {
        Generate(provisioned);
        Assert.Equal(0, await RunAsync("", [], "install.ps1"));
    }

    [Theory]
    [InlineData(true, "Get-AppxProvisionedPackage")]
    [InlineData(true, "Remove-AppxProvisionedPackage")]
    [InlineData(true, "Get-AppxPackage")]
    [InlineData(true, "Remove-AppxPackage")]
    [InlineData(false, "Get-AppxPackage")]
    [InlineData(false, "Remove-AppxPackage")]
    public async Task AnUninstallThatACmdletRefusesExitsWithAFailure(bool provisioned, string cmdlet)
    {
        Generate(provisioned);
        Assert.Equal(1, await RunAsync("", [cmdlet], "uninstall.ps1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUninstallThatSucceedsExitsWithZero(bool provisioned)
    {
        Generate(provisioned);
        Assert.Equal(0, await RunAsync("", [], "uninstall.ps1"));
    }

    [Fact]
    public void NoAppxCmdletInAGeneratedWrapperRunsWithoutStoppingOnAnError()
    {
        foreach (var provisioned in new[] { true, false })
        {
            var dir = Path.Combine(_dir, "scan-" + provisioned);
            Directory.CreateDirectory(dir);
            var package = MsixFixture.WritePackage(dir, "contoso.msix", MsixFixture.AppxManifest(name: "Contoso.Tool", publisher: Publisher));
            new WrapperGeneratorService().GenerateMsixWrappers(dir, Path.GetFileName(package), null, provisioned, PublisherId);
            foreach (var name in new[] { "install.ps1", "uninstall.ps1" })
            {
                foreach (var line in File.ReadAllLines(Path.Combine(dir, name)))
                {
                    // Each cmdlet call carries its own -ErrorAction Stop before the next pipe or block end.
                    foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(
                                 line, @"(?:Add|Remove|Get)-Appx(?:Provisioned)?Package[^|}]*"))
                        Assert.True(call.Value.Contains("-ErrorAction Stop"),
                            $"{name} (provisioned={provisioned}) has a cmdlet without -ErrorAction Stop: {call.Value}");
                }
            }
        }
    }
}
