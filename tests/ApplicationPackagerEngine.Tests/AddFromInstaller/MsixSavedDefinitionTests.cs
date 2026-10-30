using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// A saved MSIX definition keeps the uninstall command and the MSIX rule that the review shows, and the stage uses
/// both: the uninstall wrapper runs the saved command, and the rule names the provisioning key of the package.
/// </summary>
public sealed class MsixSavedDefinitionTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    private string DownloadRoot => Path.Combine(_bed.Root, "download");

    private static AppDefinition Read(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    private static StagingService Staging() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());

    private async Task<(AddAnalysisResult Review, AppDefinition Definition)> SavedAsync(string installer)
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(installer, NoProgress, CancellationToken.None);
        var saved = await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);
        return (r, Read(saved.FilePath));
    }

    private static byte[] Signature() => MsixFixture.SignatureBlob().P7x;

    // ---- the saved definition keeps the uninstall command and the MSIX rule --------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheSavedDefinition_KeepsTheUninstallCommandAndTheMsixRuleOfTheReview(bool bundle)
    {
        var (r, def) = await SavedAsync(bundle ? _bed.MsixBundle() : _bed.Msix(withDependency: false));

        Assert.Equal(DetectionKind.MsixPackage, r.Detection.Kind);
        Assert.False(string.IsNullOrWhiteSpace(r.UninstallCommand?.Value));
        Assert.Equal(r.UninstallCommand!.Value, def.UninstallCommand);
        Assert.Equal("RegistryKey", def.Detection.Type);
        Assert.Equal("HKLM", def.Detection.RegistryHive);
        Assert.Equal(StagingService.MsixProvisioningKeyTemplate, def.Detection.RegistryKeyTemplate);
    }

    [Fact]
    public async Task TheStagedUninstall_RunsTheUninstallCommandOfTheDefinition()
    {
        var (_, def) = await SavedAsync(_bed.MsixBundle(signature: Signature()));
        def.UninstallCommand = "Get-AppxPackage -AllUsers -Name 'Contoso.Tool' | Remove-AppxPackage -AllUsers";

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.True(result.Success, result.ErrorMessage);
        var uninstall = File.ReadAllText(Path.Combine(result.ContentPath!, "uninstall.ps1"));
        Assert.Contains(def.UninstallCommand, uninstall);
        Assert.DoesNotContain("AppxBundleManifest.xml", uninstall);
    }

    [Fact]
    public async Task AnMsixRegistryRuleOnAnotherKey_IsRefusedAtStage()
    {
        var (_, def) = await SavedAsync(_bed.MsixBundle(signature: Signature()));
        def.Detection.RegistryKeyTemplate = @"SOFTWARE\Contoso\Tool";

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains(StagingService.MsixProvisioningKeyTemplate, result.ErrorMessage);
    }

    [Theory]
    [InlineData("Write-Error 'removal failed'", 1)]
    [InlineData("cmd.exe /c exit 5", 5)]
    [InlineData("Write-Output 'removed'", 0)]
    public async Task TheStagedUninstall_ExitsWithTheResultOfTheUninstallCommandOfTheDefinition(string command, int expected)
    {
        var dir = Path.Combine(_bed.Root, "wrappers");
        Directory.CreateDirectory(dir);
        new WrapperGeneratorService().GenerateMsixWrappers(dir, "contoso.msixbundle", "AABB", provisioned: true, publisherId: null,
            uninstallCommand: command);

        var (exitCode, _, _) = await WindowsPowerShellHost.RunAsync(
            $"& '{Path.Combine(dir, "uninstall.ps1")}'\r\nexit $LASTEXITCODE", null, CancellationToken.None, "apd-msix-saved-uninstall");

        Assert.Equal(expected, exitCode);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(Path.Combine(dir, "exitcode.txt")));
    }
}
