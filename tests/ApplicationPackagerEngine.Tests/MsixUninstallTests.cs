using System.IO.Compression;
using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// An MSIX package installed with Add-AppxProvisionedPackage stays detected until the provisioning entry is removed and
/// every user registration is removed. The proposed uninstall command and the staged uninstall wrapper both remove
/// the provisioning entry first, then the package for all users. The AppX cmdlets run as Windows PowerShell 5.1
/// functions of the same name that record their calls, so nothing on this computer changes.
/// </summary>
public sealed class MsixUninstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msix-uninstall-tests-" + Guid.NewGuid().ToString("N"));

    public MsixUninstallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Publisher = "CN=Contoso";
    private static readonly string PublisherId = MsixPackageReader.PublisherHash(Publisher);

    /// <summary>
    /// Functions that shadow the AppX cmdlets: the package is provisioned and registered, and a package of the same
    /// name from another publisher is provisioned and registered too. Get-AppxPackage returns the Bundle package only
    /// when the filter asks for it.
    /// </summary>
    private static string Stubs(string name) => $$"""
        function Get-AppxProvisionedPackage { param([switch]$Online)
            [pscustomobject]@{ DisplayName = '{{name}}'; PackageName = '{{name}}_1.0.0.0_neutral_~_{{PublisherId}}' }
            [pscustomobject]@{ DisplayName = '{{name}}'; PackageName = '{{name}}_1.0.0.0_neutral_~_otherpublisher1' }
            [pscustomobject]@{ DisplayName = 'Other.App'; PackageName = 'Other.App_1.0.0.0_neutral_~_{{PublisherId}}' } }
        function Remove-AppxProvisionedPackage { param([switch]$Online, [string]$PackageName) [Console]::Out.WriteLine('deprovision ' + $PackageName + ' online=' + $Online) }
        function Get-AppxPackage { param([switch]$AllUsers, [string]$Name, [string[]]$PackageTypeFilter)
            if ($Name -ne '{{name}}') { return }
            $type = if ($PackageTypeFilter -contains 'Bundle') { 'bundle' } else { 'x64' }
            [pscustomobject]@{ PackageFullName = "{{name}}_1.0.0.0_$($type)__{{PublisherId}}"; PublisherId = '{{PublisherId}}' }
            [pscustomobject]@{ PackageFullName = "{{name}}_1.0.0.0_$($type)__otherpublisher1"; PublisherId = 'otherpublisher1' } }
        function Remove-AppxPackage { param([string]$Package, [switch]$AllUsers) [Console]::Out.WriteLine('remove ' + $Package + ' allusers=' + $AllUsers) }
        """;

    private static async Task<IReadOnlyList<string>> RunAsync(string script)
    {
        var (exitCode, stdOut, stdErr) = await WindowsPowerShellHost.RunAsync(script, null, CancellationToken.None, "apd-msixuninstall");
        Assert.True(exitCode == 0, $"exit {exitCode}: {stdErr}");
        return stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] Removed(bool bundle) =>
    [
        $"deprovision Contoso.Tool_1.0.0.0_neutral_~_{PublisherId} online=True",
        $"remove Contoso.Tool_1.0.0.0_{(bundle ? "bundle" : "x64")}__{PublisherId} allusers=True"
    ];

    private string Bundle()
    {
        var path = Path.Combine(_dir, "contoso.msixbundle");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("AppxMetadata/AppxBundleManifest.xml").Open(), Encoding.UTF8);
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle" SchemaVersion="5.0">
              <Identity Name="Contoso.Tool" Publisher="{Publisher}" Version="1.0.0.0" />
              <Packages><Package Type="application" Version="1.0.0.0" Architecture="x64" FileName="Contoso.Tool_x64.msix" /></Packages>
            </Bundle>
            """);
        return path;
    }

    private string Package(bool bundle) => bundle
        ? Bundle()
        : MsixFixture.WritePackage(_dir, "contoso.msix", MsixFixture.AppxManifest(name: "Contoso.Tool", publisher: Publisher));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheProposedUninstallCommand_RemovesTheProvisioningEntryAndTheRegistrationOfEveryUser_OfThisPublisherOnly(bool bundle)
    {
        var report = await new InstallerAnalysisService(new AnalysisSandbox(Path.Combine(_dir, "sandbox")))
            .AnalyzeAsync(Package(bundle), AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);
        var command = report.Deployment.Single(f => f.Name == "UninstallCommand").Value;

        var calls = await RunAsync(Stubs("Contoso.Tool") + "\r\n" + command);

        Assert.Equal(Removed(bundle), calls);
    }

    [Fact]
    public async Task TheReviewNamesTheRemovalDocumentationAsTheSourceOfTheUninstallCommand()
    {
        using var bed = new AddFromInstallerTestBed();
        var r = await bed.Real().AnalyzeAsync(bed.Msix(name: "Contoso.Tool", withDependency: false), new Progress<string>(), CancellationToken.None);

        Assert.Equal("https://learn.microsoft.com/powershell/module/dism/remove-appxprovisionedpackage", r.UninstallCommand!.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheStagedUninstallWrapper_RemovesTheProvisioningEntryAndTheRegistrationOfEveryUser_OfThisPublisherOnly(bool bundle)
    {
        var package = Package(bundle);
        new WrapperGeneratorService().GenerateMsixWrappers(_dir, Path.GetFileName(package), "AABB", provisioned: true, publisherId: PublisherId);

        var calls = await RunAsync(Stubs("Contoso.Tool") + $"\r\n& '{Path.Combine(_dir, "uninstall.ps1")}'\r\nif ($LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}");

        Assert.Equal(Removed(bundle), calls);
    }
}
