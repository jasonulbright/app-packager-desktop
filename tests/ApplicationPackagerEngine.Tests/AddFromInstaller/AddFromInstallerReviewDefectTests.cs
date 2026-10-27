using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using static ApplicationPackagerEngine.Tests.AddFromInstaller.AddFromInstallerTestBed;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>Defects of the review and save of the Add from file and Add from web flows, one region per defect.</summary>
public sealed class AddFromInstallerReviewDefectTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    private static AppDefinition Read(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    // ---- an MSI uninstall never carries the literal ProductCode ------------------------------

    [Theory]
    [InlineData(Evidence.Confirmed, false)]
    [InlineData(Evidence.Manual, false)]
    [InlineData(Evidence.Manual, true)]
    public async Task MsiUninstall_NeverSavesTheLiteralProductCode(Evidence evidence, bool lowerCase)
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var literal = lowerCase ? r.UninstallArguments.Value.ToLowerInvariant() : r.UninstallArguments.Value;
        Assert.Contains(ProductCode, r.UninstallArguments.Value, StringComparison.OrdinalIgnoreCase);

        var fromWeb = await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions) { UninstallArgs = literal }, CancellationToken.None);
        var web = Read(fromWeb.FilePath);

        Assert.DoesNotContain(ProductCode, web.UninstallArgs, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/x {ProductCode}", web.UninstallArgs);

        var edited = r with { UninstallArguments = new ProposedValue<string>(Value: literal + " /L*v log.txt", Evidence: evidence, Source: null), Name = r.Name with { Value = "Second App", Evidence = Evidence.Manual } };
        var fromFile = Read((await service.SaveAsync(edited, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None)).FilePath);
        if (evidence == Evidence.Manual)
        {
            Assert.DoesNotContain(ProductCode, fromFile.UninstallArgs, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("{ProductCode}", fromFile.UninstallArgs);
            Assert.Contains("/L*v log.txt", fromFile.UninstallArgs);
        }
    }

    [Fact]
    public async Task MsiUninstall_AnotherGuidTheUserTypedStaysAsTyped()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        const string other = "{99999999-8888-4777-8666-555555555555}";

        var def = Read((await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions) { UninstallArgs = $"/x {other} /qn" }, CancellationToken.None)).FilePath);

        Assert.Equal($"/x {other} /qn", def.UninstallArgs);
    }
}
