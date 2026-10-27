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

    // ---- the saved architecture is one the stage accepts -------------------------------------

    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("X86", "x86")]
    [InlineData("arm64", "ARM64")]
    [InlineData("neutral", "x64")]
    [InlineData("NEUTRAL", "x64")]
    [InlineData("", "x64")]
    public async Task MsixArchitecture_IsSavedAsOneTheStageAccepts(string declared, string expected)
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msix(architecture: declared), NoProgress, CancellationToken.None);

        var def = Read((await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None)).FilePath);

        Assert.Equal(expected, def.Architecture);
        Assert.Equal(expected, InstallerArchitectures.Resolve(def.Architecture));
        if (declared.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            Assert.Contains(r.Notes, n => n.Contains("neutral", StringComparison.OrdinalIgnoreCase) && n.Contains("x64"));
    }

    [Theory]
    [InlineData("arm")]
    [InlineData("ARM")]
    [InlineData("riscv")]
    public async Task MsixArchitecture_TheStageCannotUse_NeedsReviewAndIsRefusedAtSave(string declared)
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msix(architecture: declared), NoProgress, CancellationToken.None);

        Assert.True(r.NeedsReview);
        Assert.Contains("architecture", r.ReviewReason, StringComparison.OrdinalIgnoreCase);
        var ex = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() =>
            service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None));
        Assert.Contains("x86, x64 and ARM64", ex.Message);
        Assert.False(Directory.Exists(_bed.Definitions) && Directory.EnumerateFiles(_bed.Definitions).Any());
    }

    [Fact]
    public async Task AnArchitectureTypedInTheReview_ThatTheStageCannotUse_IsRefused()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var edited = r with { Architecture = new ProposedValue<string>(Value: "sparc", Evidence: Evidence.Manual, Source: null) };

        await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() =>
            service.SaveAsync(edited, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None));
    }
}
