using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class SevenZipDefinitionTests
{
    private const string ExpectedX64MsiHash = "c0680064d698a62dd4a5a47f403db356a6531a5473e4c4b1d090ea2590513926";

    [Fact]
    public void DefinitionRequiresTheDigestForTheVersionedX64Msi()
    {
        var definition = DefinitionFixture.Load("7zip.json");

        Assert.True(definition.RequirePayloadHash);
        var query = Assert.IsType<string>(definition.VersionScraper.PayloadHashQuery);
        Assert.Contains("7z{version}-x64\\.msi", query);
        Assert.Contains("sha256:", query);
    }

    [Fact]
    public void FeedHashQuerySelectsOnlyTheMatchingX64MsiAsset()
    {
        var definition = DefinitionFixture.Load("7zip.json");
        var release = Release(Asset("7z2603-x64.exe", new string('1', 64)), Asset("7z2603-x64.msi", ExpectedX64MsiHash), Asset("7z2603.msi", new string('2', 64)));

        Assert.Equal(ExpectedX64MsiHash, StagingService.ExtractFeedHash(release, definition.VersionScraper, "2603"));
    }

    [Fact]
    public void FeedHashQueryDoesNotBorrowAnotherAssetsDigestWhenTheMsiDigestIsMissing()
    {
        var definition = DefinitionFixture.Load("7zip.json");
        var release = Release(Asset("7z2603-x64.msi", null), Asset("7z2603.msi", new string('2', 64)));

        Assert.Null(StagingService.ExtractFeedHash(release, definition.VersionScraper, "2603"));
    }

    [Fact]
    public void DefinitionDeclaresAnOptInArm64ExeWithIndependentDetectionAndRequiredDigest()
    {
        var definition = DefinitionFixture.Load("7zip.json");
        var variant = Assert.Single(definition.PayloadVariants!);

        Assert.Contains("Architecture", definition.SupportsVariants!);
        Assert.Equal("ARM64", variant.NameSuffix);
        Assert.Equal("ARM64", variant.Architecture);
        Assert.Equal("arm64", variant.ContentSubpath);
        Assert.Equal("EXE", variant.InstallerType);
        Assert.Contains("7z{version}-arm64.exe", variant.DownloadUrl);
        Assert.Equal("/S", variant.InstallArgs);
        Assert.Equal("/S", variant.UninstallArgs);
        Assert.Contains("\\Uninstall.exe", variant.UninstallCommand);
        Assert.Equal("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\7-Zip", variant.Detection.RegistryKeyTemplate);
        Assert.True(variant.RequirePayloadHash);
        Assert.Contains("7z{version}-arm64\\.exe", variant.PayloadHashQuery);
    }

    [Fact]
    public void FeedHashQuerySelectsOnlyTheMatchingArm64ExeAsset()
    {
        var definition = DefinitionFixture.Load("7zip.json");
        var variant = Assert.Single(definition.PayloadVariants!);
        var hash = new string('A', 64);
        var release = Release(Asset("7z2603-x64.exe", new string('1', 64)), Asset("7z2603-arm64.exe", hash), Asset("7z2603-arm.exe", new string('2', 64)));
        var scraper = new VersionScraperConfig
        {
            Type = definition.VersionScraper.Type,
            PayloadHashQuery = variant.PayloadHashQuery
        };

        Assert.Equal(hash, StagingService.ExtractFeedHash(release, scraper, "2603"));

        var missingArm64Digest = Release(Asset("7z2603-x64.exe", new string('1', 64)), Asset("7z2603-arm64.exe", null), Asset("7z2603-arm.exe", new string('2', 64)));
        Assert.Null(StagingService.ExtractFeedHash(missingArm64Digest, scraper, "2603"));
    }

    [Fact]
    public void PayloadArchitectureMustMatchItsDeclaredVariant()
    {
        var path = Path.Combine(Path.GetTempPath(), $"apkg-arm64-{Guid.NewGuid():N}.exe");
        try
        {
            var bytes = new byte[0x100];
            bytes[0] = 0x4D; bytes[1] = 0x5A;
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
            BitConverter.GetBytes(0x00004550).CopyTo(bytes, 0x80);
            BitConverter.GetBytes((ushort)0xAA64).CopyTo(bytes, 0x84);
            File.WriteAllBytes(path, bytes);

            StagingService.VerifyPayloadArchitecture(path, "ARM64");
            var mismatch = Assert.Throws<InvalidOperationException>(() => StagingService.VerifyPayloadArchitecture(path, "x64"));
            Assert.Contains("expected 'x64'", mismatch.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ArchitectureSplitCannotPublishWhenOptionalPayloadWasNotStaged()
    {
        var manifest = new StageManifest
        {
            HasOptionalArchitecturePayloads = true,
            OptionalArchitecturePayloadsStaged = false
        };

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            DeploymentConditionsService.ApplyVariantSplit(manifest,
                new DeploymentConditionEntry { Split = "Architecture" }, Path.GetTempPath()));

        Assert.Contains("Re-stage", refusal.Message);
    }

    [Fact]
    public void ArchitectureSplitRefusesLegacyManifestWhenDefinitionDeclaresOptionalPayloads()
    {
        var manifest = new StageManifest();

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            DeploymentConditionsService.ApplyVariantSplit(manifest,
                new DeploymentConditionEntry { Split = "Architecture" }, Path.GetTempPath(),
                definitionDeclaresOptionalArchitecturePayloads: true));

        Assert.Contains("Re-stage", refusal.Message);
    }

    [Fact]
    public void ArchitectureSplitStillAppliesToLegacyDefinitionsWithoutOptionalPayloads()
    {
        var manifest = new StageManifest();

        DeploymentConditionsService.ApplyVariantSplit(manifest,
            new DeploymentConditionEntry { Split = "Architecture" }, Path.GetTempPath());

        Assert.Equal(["ARM64", "x64"], manifest.DeploymentTypes!.Select(type => type.NameSuffix));
    }

    [Fact]
    public async Task StagingArchitectureSplitCreatesSeparateArm64PayloadAndCanBeDisabledOnRestage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apkg-7zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var definition = DefinitionFixture.Load("7zip.json");
            var version = "24.08";
            var baseInstaller = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WsusRules", "seven-zip.msi");
            var appFolder = $"{definition.Application.Replace(" ", "", StringComparison.Ordinal)}-{definition.Architecture}";
            var content = Path.Combine(root, appFolder, version);
            var arm64 = Path.Combine(content, "arm64");
            Directory.CreateDirectory(arm64);

            definition.VersionScraper.VersionTransform = null;
            definition.VersionScraper.PayloadHashQuery = null;
            definition.PayloadSha256 = StagingService.ComputeSha256(baseInstaller);
            definition.DownloadUrl = "https://github.com/ip7z/7zip/releases/download/24.08/7z2408-x64.msi";
            var variant = Assert.Single(definition.PayloadVariants!);
            var arm64Bytes = SyntheticPe(0xAA64);
            variant.DownloadUrl = "https://github.com/ip7z/7zip/releases/download/24.08/7z2408-arm64.exe";
            variant.PayloadHashQuery = "unused-in-this-pinned-payload-test";
            variant.PayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(arm64Bytes));

            File.Copy(baseInstaller, Path.Combine(content, "7z2408-x64.msi"));
            File.WriteAllBytes(Path.Combine(arm64, "7z2408-arm64.exe"), arm64Bytes);

            var service = new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
                new WrapperGeneratorService(), new ManifestService());
            var first = await service.StageAsync(definition, root, knownVersion: version,
                inputs: new StageInputs { IncludeArchitectureVariants = true });

            Assert.True(first.Success, first.ErrorMessage);
            var manifest = new ManifestService().Read(first.ManifestPath!);
            Assert.True(manifest.HasOptionalArchitecturePayloads);
            Assert.True(manifest.OptionalArchitecturePayloadsStaged);
            var deploymentTypes = Assert.IsType<StageDeploymentType[]>(manifest.DeploymentTypes);
            Assert.Equal(["ARM64", "x64"], deploymentTypes.Select(dt => dt.NameSuffix));
            Assert.Equal("arm64", deploymentTypes[0].ContentSubpath);
            Assert.Null(deploymentTypes[1].Detection);
            Assert.Null(deploymentTypes[1].InstallCommandLine);
            Assert.Equal("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\7-Zip", deploymentTypes[0].Detection!.RegistryKeyRelative);
            Assert.Equal("24.08", deploymentTypes[0].Detection!.ExpectedValue);
            Assert.False(string.IsNullOrWhiteSpace(manifest.ProductCode));
            var specs = DeploymentTypeSpecResolver.Resolve(manifest, @"\\server\Applications\7-Zip", manifest.AppName);
            Assert.Equal(2, specs.Count);
            Assert.Equal(Path.Combine(@"\\server\Applications\7-Zip", "arm64"), specs[0].ContentLocation);
            Assert.Equal("install.bat", specs[0].InstallCommand);
            Assert.Equal("uninstall.bat", specs[0].UninstallCommand);
            Assert.Equal("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\7-Zip", specs[0].Detection.RegistryKeyRelative);
            Assert.Equal("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + manifest.ProductCode, specs[1].Detection.RegistryKeyRelative);
            Assert.Contains(manifest.FileHashes!, file => file.RelativePath.Equals(Path.Combine("arm64", "7z2408-arm64.exe"), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                [("7z2408-x64.msi", definition.PayloadSha256), (Path.Combine("arm64", "7z2408-arm64.exe"), variant.PayloadSha256)],
                manifest.VerifiedPayloads!.Select(p => (p.RelativePath, (string?)p.Sha256)));
            Assert.True(File.Exists(Path.Combine(arm64, "install.bat")));
            Assert.True(File.Exists(Path.Combine(arm64, "uninstall.bat")));
            Assert.Contains("/S", File.ReadAllText(Path.Combine(arm64, "install.ps1")));
            Assert.Contains(@"C:\Program Files\7-Zip\Uninstall.exe", File.ReadAllText(Path.Combine(arm64, "uninstall.ps1")));

            var second = await service.StageAsync(definition, root, knownVersion: version,
                inputs: new StageInputs { IncludeArchitectureVariants = false });

            Assert.True(second.Success, second.ErrorMessage);
            var singlePayloadManifest = new ManifestService().Read(second.ManifestPath!);
            Assert.True(singlePayloadManifest.HasOptionalArchitecturePayloads);
            Assert.False(singlePayloadManifest.OptionalArchitecturePayloadsStaged);
            Assert.Null(singlePayloadManifest.DeploymentTypes);
            Assert.False(File.Exists(Path.Combine(arm64, "7z2408-arm64.exe")));
            Assert.False(File.Exists(Path.Combine(arm64, "install.bat")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheInstallWrapperOfAnArchitecturePayloadReportsTheDefinitionsSuccessCodesAsZero()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apkg-7zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var definition = DefinitionFixture.Load("7zip.json");
            var version = "24.08";
            var baseInstaller = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WsusRules", "seven-zip.msi");
            var content = Path.Combine(root, $"{definition.Application.Replace(" ", "", StringComparison.Ordinal)}-{definition.Architecture}", version);
            var arm64 = Path.Combine(content, "arm64");
            Directory.CreateDirectory(arm64);

            definition.VersionScraper.VersionTransform = null;
            definition.VersionScraper.PayloadHashQuery = null;
            definition.PayloadSha256 = StagingService.ComputeSha256(baseInstaller);
            definition.DownloadUrl = "https://github.com/ip7z/7zip/releases/download/24.08/7z2408-x64.msi";
            definition.SuccessExitCodes = [111111];
            var variant = Assert.Single(definition.PayloadVariants!);
            var arm64Bytes = SyntheticPe(0xAA64);
            variant.DownloadUrl = "https://github.com/ip7z/7zip/releases/download/24.08/7z2408-arm64.exe";
            variant.PayloadHashQuery = "unused-in-this-pinned-payload-test";
            variant.PayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(arm64Bytes));
            File.Copy(baseInstaller, Path.Combine(content, "7z2408-x64.msi"));
            File.WriteAllBytes(Path.Combine(arm64, "7z2408-arm64.exe"), arm64Bytes);

            var service = new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
                new WrapperGeneratorService(), new ManifestService());
            var result = await service.StageAsync(definition, root, knownVersion: version,
                inputs: new StageInputs { IncludeArchitectureVariants = true });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains("if ($proc.ExitCode -eq 111111) { '0' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit 0 }",
                File.ReadAllText(Path.Combine(arm64, "install.ps1")));
            Assert.DoesNotContain("111111", File.ReadAllText(Path.Combine(arm64, "uninstall.ps1")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task APayloadReplacedAfterItsDigestCheckGetsNoManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apkg-7zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var definition = DefinitionFixture.Load("7zip.json");
            var version = "24.08";
            var baseInstaller = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WsusRules", "seven-zip.msi");
            var content = Path.Combine(root, $"{definition.Application.Replace(" ", "", StringComparison.Ordinal)}-{definition.Architecture}", version);
            var arm64 = Path.Combine(content, "arm64");
            Directory.CreateDirectory(arm64);

            definition.VersionScraper.VersionTransform = null;
            definition.VersionScraper.PayloadHashQuery = null;
            definition.PayloadSha256 = StagingService.ComputeSha256(baseInstaller);
            definition.DownloadUrl = "https://github.com/ip7z/7zip/releases/download/24.08/7z2408-x64.msi";
            var variant = Assert.Single(definition.PayloadVariants!);
            var arm64Bytes = SyntheticPe(0xAA64);
            variant.DownloadUrl = "https://github.com/ip7z/7zip/releases/download/24.08/7z2408-arm64.exe";
            variant.PayloadHashQuery = "unused-in-this-pinned-payload-test";
            variant.PayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(arm64Bytes));
            var arm64Installer = Path.Combine(arm64, "7z2408-arm64.exe");
            File.Copy(baseInstaller, Path.Combine(content, "7z2408-x64.msi"));
            File.WriteAllBytes(arm64Installer, arm64Bytes);

            var replacement = SyntheticPe(0xAA64);
            replacement[^1] = 0x01;
            void ReplaceAfterVerification(string line)
            {
                if (line.StartsWith("Optional architecture payload", StringComparison.Ordinal))
                    File.WriteAllBytes(arm64Installer, replacement);
            }

            var service = new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
                new WrapperGeneratorService(), new ManifestService());
            var result = await service.StageAsync(definition, root, ReplaceAfterVerification, knownVersion: version,
                inputs: new StageInputs { IncludeArchitectureVariants = true });

            Assert.False(result.Success);
            Assert.Contains("vendor SHA256 mismatch", result.ErrorMessage);
            Assert.Contains("7z2408-arm64.exe", result.ErrorMessage);
            Assert.False(File.Exists(Path.Combine(content, "stage-manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AVerifiedPayloadMissingAtTheManifestWriteRefusesTheStage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apkg-7zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var manifestPath = Path.Combine(root, "stage-manifest.json");
            var finalizer = ApplicationPackagerEngine.Services.Workbench.StageFinalizer.Begin(null);
            finalizer.RecordVerifiedPayload(Path.Combine(root, "7z2408-arm64.exe"), new string('A', 64));

            var refusal = Assert.Throws<InvalidOperationException>(() =>
                finalizer.FinalizeAndWrite(new ManifestService(), manifestPath, new StageManifest()));

            Assert.Contains("verified payload missing: 7z2408-arm64.exe", refusal.Message);
            Assert.False(File.Exists(manifestPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AStagedPayloadThatNoLongerCarriesItsVendorDigestIsRefusedBeforeContentSync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apkg-7zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var installer = Path.Combine(root, "7z2408-arm64.exe");
            File.WriteAllBytes(installer, SyntheticPe(0xAA64));
            var manifestPath = Path.Combine(root, "stage-manifest.json");
            var manifest = new StageManifest
            {
                VerifiedPayloads = [new StageVerifiedPayload { RelativePath = "7z2408-arm64.exe", Sha256 = StagingService.ComputeSha256(installer) }]
            };
            new ManifestService().Write(manifestPath, manifest);
            Assert.Empty(ContentSyncService.Verify(root, manifest));

            // The replacement and a matching FileHashes record, as an account with write access could leave them.
            File.WriteAllBytes(installer, SyntheticPe(0x8664));
            manifest.FileHashes = ManifestService.ComputeFileHashes(root, "stage-manifest.json");

            Assert.Equal(["vendor SHA256 mismatch: 7z2408-arm64.exe"], ContentSyncService.Verify(root, manifest));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The field order of the GitHub release API: the uploader object, whose URL templates
    // carry braces, sits between an asset's name and its digest.
    private static string Asset(string name, string? digest) =>
        $$"""{ "url": "https://api.github.com/repos/ip7z/7zip/releases/assets/1", "id": 1, "name": "{{name}}", "label": null, "uploader": { "login": "ip7z", "following_url": "https://api.github.com/users/ip7z/following{/other_user}", "gists_url": "https://api.github.com/users/ip7z/gists{/gist_id}" }, "content_type": "application/octet-stream", "size": 1, "digest": {{(digest is null ? "null" : $"\"sha256:{digest}\"")}}, "download_count": 0 }""";

    private static string Release(params string[] assets) => $$"""{ "tag_name": "26.03", "author": { "login": "ip7z" }, "assets": [{{string.Join(",", assets)}}] }""";

    private static byte[] SyntheticPe(ushort machine)
    {
        var bytes = new byte[0x100];
        bytes[0] = 0x4D; bytes[1] = 0x5A;
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        BitConverter.GetBytes(0x00004550).CopyTo(bytes, 0x80);
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }
}
