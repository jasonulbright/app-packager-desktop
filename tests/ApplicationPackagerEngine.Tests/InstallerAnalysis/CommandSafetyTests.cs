using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.InstallerScripts;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>Text read from an installer reaches a proposed command line only after it is validated.</summary>
public sealed class CommandSafetyTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;
    private static readonly AnalysisLimits Limits = AnalysisLimits.Default with { Timeout = TimeSpan.FromSeconds(60) };

    public CommandSafetyTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox, () => null);
    }

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private string Chocolatey(string name, string id, string version)
    {
        var nuspec = $"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd\"><metadata><id>{System.Security.SecurityElement.Escape(id)}</id><version>{System.Security.SecurityElement.Escape(version)}</version><authors>Contoso</authors><description>d</description></metadata></package>";
        return WriteZip(_temp.PathOf(name), ("pkg.nuspec", Text(nuspec)), ("tools/chocolateyInstall.ps1", Text("Write-Host x")));
    }

    private static AnalysisFact Field(AnalysisReport report, string name) => report.Deployment.Single(f => f.Name == name);

    [Fact]
    public async Task AValidChocolateyPackageGetsPredictedCommands()
    {
        var report = await _service.AnalyzeAsync(Chocolatey("good.nupkg", "contoso.tool", "1.2.3"), Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("Chocolatey", report.Type);
        Assert.Equal("choco install contoso.tool --version=1.2.3 -y --source=\"<SourceDirOrFeed>\"", Field(report, "InstallCommand").Value);
        Assert.Equal("Predicted", Field(report, "InstallCommand").Confidence);
        Assert.Equal("choco uninstall contoso.tool -y", Field(report, "UninstallCommand").Value);
        Assert.Equal("Predicted", Field(report, "UninstallCommand").Confidence);
    }

    [Theory]
    [InlineData("contoso & calc", "1.0.0")]
    [InlineData("contoso\"tool", "1.0.0")]
    [InlineData("contoso.tool", "1.0 & calc")]
    [InlineData("contoso.tool", "1.0.0;calc")]
    public async Task AChocolateyPackageWithAnInvalidIdOrVersionGetsNoCommandAndANote(string id, string version)
    {
        var report = await _service.AnalyzeAsync(Chocolatey("bad-" + Guid.NewGuid().ToString("N")[..6] + ".nupkg", id, version), Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("", Field(report, "InstallCommand").Value);
        Assert.Equal("None", Field(report, "InstallCommand").Confidence);
        Assert.DoesNotContain("calc", Field(report, "UninstallCommand").Value);
        Assert.DoesNotContain("calc", report.DetectionHint);
        Assert.Contains(report.Notes, n => n.StartsWith("The package id or version is not a valid NuGet value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnMsixWithAnInvalidIdentityNameGetsNoUninstallCommand()
    {
        var manifest = "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"A';calc;'B\" Publisher=\"CN=A\" Version=\"1.0.0.0\" ProcessorArchitecture=\"x64\" /></Package>";
        var path = WriteZip(_temp.PathOf("bad.msix"), ("AppxManifest.xml", Text(manifest)), ("app.exe", new byte[16]));

        var report = await _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("", Field(report, "UninstallCommand").Value);
        Assert.DoesNotContain("calc", report.DetectionHint);
        Assert.Contains(report.Notes, n => n.StartsWith("The MSIX identity name is not a valid package name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AValidMsixGetsAPredictedUninstallCommand()
    {
        var manifest = "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"Contoso.Tool\" Publisher=\"CN=A\" Version=\"1.0.0.0\" ProcessorArchitecture=\"x64\" /></Package>";
        var path = WriteZip(_temp.PathOf("good.msix"), ("AppxManifest.xml", Text(manifest)), ("app.exe", new byte[16]));

        var report = await _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("Get-AppxPackage -Name 'Contoso.Tool' | Remove-AppxPackage", Field(report, "UninstallCommand").Value);
        Assert.Equal("Predicted", Field(report, "UninstallCommand").Confidence);
    }

    [Fact]
    public async Task AnMsiWithAProductCodeThatIsNotAGuidGetsNoUninstallCommand()
    {
        var msi = _temp.PathOf("badcode.msi");
        using (var b = new TestMsiBuilder(msi))
            b.Property("ProductCode", "{X\" & calc & \"}").Property("ProductName", "Bad").Property("ProductVersion", "1.0.0").Property("ALLUSERS", "1").Template("x64;1033");

        var report = await _service.AnalyzeAsync(msi, Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("", Field(report, "UninstallCommand").Value);
        Assert.All(report.Modes, m => Assert.DoesNotContain("calc", m.UninstallCommand));
        Assert.Contains(report.Notes, n => n.StartsWith("The ProductCode is not a GUID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\uninst.exe\" /S", true)]
    [InlineData("\"%LOCALAPPDATA%\\App\\Update.exe\" --uninstall -s", true)]
    [InlineData("msiexec.exe /x \"{23170F69-40C1-2702-2408-000001000000}\" /qn /norestart", true)]
    [InlineData("\"C:\\App\\a\" & calc & \"b.exe\" /S", false)]
    [InlineData("\"C:\\App\\uninst.exe /S", false)]
    [InlineData("C:\\App\\uninst.exe /S | calc", false)]
    [InlineData("\"C:\\App\\uninst.exe\" /S\r\ncalc", false)]
    [InlineData("C:\\App\\uninst.exe /S ^& calc", false)]
    public void ACommandLineIsSafeOnlyWithoutShellOperatorsOutsideQuotes(string command, bool safe)
    {
        Assert.Equal(safe, ProposedCommand.IsSafeCommandLine(command));
    }

    [Theory]
    [InlineData(@"%ProgramFiles%\Contoso\unins000.exe", true)]
    [InlineData(@"%ProgramFiles%\Con""toso\unins000.exe", false)]
    [InlineData("%ProgramFiles%\\Contoso\nx\\unins000.exe", false)]
    [InlineData(@"%ProgramFiles%\Contoso|x\unins000.exe", false)]
    public void APathIsQuotableOnlyWithoutQuotesControlCharactersOrPipes(string path, bool quotable)
    {
        Assert.Equal(quotable, CommandLineText.IsQuotablePath(path));
    }
}
