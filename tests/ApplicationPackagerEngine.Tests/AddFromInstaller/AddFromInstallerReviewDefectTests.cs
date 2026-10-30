using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
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

    // ---- the package signature is bounded and described as what was checked -------------------

    [Fact]
    public async Task AnOversizedSignaturePart_GivesUnknownTrustAndNoException()
    {
        var service = _bed.Real();
        var big = new byte[ApplicationPackagerEngine.Services.MsixPackageReader.MaxSignatureBytes + 1];
        Random.Shared.NextBytes(big);

        var r = await service.AnalyzeAsync(_bed.Msix(signature: big), NoProgress, CancellationToken.None);

        Assert.Equal(PackageTrust.Unknown, r.Package!.Trust);
        Assert.Contains(r.Notes, n => n.Contains("signature could not be read"));
    }

    [Fact]
    public async Task AnInvalidSignature_IsNotDescribedAsADigestCheckOfThePackageContents()
    {
        var service = _bed.Real();
        var (blob, _) = MsixFixture.SignatureBlob();
        blob[^1] ^= 0xFF;

        var r = await service.AnalyzeAsync(_bed.Msix(signature: blob), NoProgress, CancellationToken.None);

        Assert.Equal(PackageTrust.Invalid, r.Package!.Trust);
        Assert.DoesNotContain("contents", r.Package.TrustWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not valid", r.Package.TrustWarning);
        Assert.True(r.NeedsReview);
    }

    // ---- a detection the user edited survives a change of the install mode -----------------------

    private static DetectionProposal EditedRule() => new(
        Kind: DetectionKind.RegistryVersion, Description: "edited",
        Values: new Dictionary<string, string>
        {
            ["Hive"] = "HKLM", ["Key"] = @"SOFTWARE\Edited\Key", ["ValueName"] = "DisplayVersion", ["Operator"] = "GreaterEquals", ["Expected"] = "9.9", ["Is64Bit"] = "true"
        },
        Evidence: Evidence.Manual, Source: null);

    [Fact]
    public async Task ChangedInstallMode_KeepsTheDetectionTheUserEdited()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Fixture("nsis-u-multimode.bin", "TestApp-Setup.exe"), NoProgress, CancellationToken.None);
        Assert.NotEqual("AllUsers", r.DefaultInstallMode);

        var def = Read((await service.SaveAsync(r with { Detection = EditedRule() },
            new AddSaveOptions(DefinitionsFolder: _bed.Definitions) { InstallMode = "AllUsers" }, CancellationToken.None)).FilePath);

        Assert.Equal(@"SOFTWARE\Edited\Key", def.Detection.RegistryKeyTemplate);
        Assert.Equal("9.9", def.Detection.ExpectedValue);
        Assert.Equal("GreaterEquals", def.Detection.Operator);
    }

    [Fact]
    public async Task ChangedInstallMode_WithAnUneditedDetection_TakesTheDetectionOfThatMode()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Fixture("nsis-u-multimode.bin", "TestApp-Setup.exe"), NoProgress, CancellationToken.None);

        var def = Read((await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions) { InstallMode = "AllUsers" }, CancellationToken.None)).FilePath);

        Assert.Equal("HKLM", def.Detection.RegistryHive);
    }

    [Fact]
    public async Task ChangedInstallMode_WithADefaultDetection_SavesNoRule()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Fixture("nsis-u-multimode.bin", "TestApp-Setup.exe"), NoProgress, CancellationToken.None);

        var def = Read((await service.SaveAsync(r with { Detection = DetectionProposal.NoneDerived("none") },
            new AddSaveOptions(DefinitionsFolder: _bed.Definitions) { InstallMode = "AllUsers" }, CancellationToken.None)).FilePath);

        Assert.Null(def.Detection.RegistryKeyTemplate);
    }

    // ---- a predicted detection and an untrusted signer need review --------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMsixSignerThisComputerDoesNotTrust_NeedsReviewInTheAnalysisAndAfterTheSave(bool oversizedSignature)
    {
        var service = _bed.Real();
        var signature = oversizedSignature
            ? new byte[ApplicationPackagerEngine.Services.MsixPackageReader.MaxSignatureBytes + 1]
            : MsixFixture.SignatureBlob().P7x;
        var r = await service.AnalyzeAsync(_bed.Msix(withDependency: false, signature: signature), NoProgress, CancellationToken.None);

        Assert.Equal(oversizedSignature ? PackageTrust.Unknown : PackageTrust.Untrusted, r.Package!.Trust);
        Assert.True(r.NeedsReview);
        Assert.Contains(r.Package.TrustWarning!, r.ReviewReason);

        var saved = await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);

        Assert.True(saved.NeedsReview);
        Assert.Contains(r.Package.TrustWarning!, saved.ReviewReason);
    }

    [Fact]
    public async Task APredictedDetection_WithADocumentedSource_NeedsReviewUntilTheUserEditsIt()
    {
        const string patchCode = "{AAAABBBB-0000-4000-8000-000000000002}";
        var path = _bed.PathOf("contoso-update.msp");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "MSP", [],
            metadata:
            [
                new MetadataBlock(Title: "MSI patch (contoso-update.msp)", Facts:
                [
                    Fact("DisplayName", "Contoso Tool Update (4.5.7)", "Confirmed", "msi-patch-metadata"),
                    Fact("ManufacturerName", "Contoso Ltd", "Confirmed", "msi-patch-metadata"),
                    Fact("Post-patch version", "4.5.7", "Predicted", "msi-patch-metadata"),
                    Fact("Primary PatchCode", patchCode, "Confirmed", "msi-summary-information"),
                    Fact("Target ProductCodes", ProductCode, "Confirmed", "msi-summary-information")
                ])
            ]);
        var service = _bed.Over(fake);

        var r = await service.AnalyzeAsync(path, NoProgress, CancellationToken.None);

        Assert.Equal(Evidence.Predicted, r.Detection.Evidence);
        Assert.False(AddFromInstallerService.IsUndocumented(r.Detection.Source));
        Assert.True(r.NeedsReview);
        Assert.Contains("Detection needs review", r.ReviewReason);

        var edited = r with { Detection = r.Detection with { Evidence = Evidence.Manual, Source = null } };
        var saved = await service.SaveAsync(edited with { Version = r.Version with { Evidence = Evidence.Manual } }, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);
        Assert.DoesNotContain("Detection needs review", saved.ReviewReason ?? "");
    }
}
