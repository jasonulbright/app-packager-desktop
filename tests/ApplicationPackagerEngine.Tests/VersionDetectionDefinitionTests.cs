using System.IO.Compression;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Eighteen packagers detect the installed version, not the presence of a file or
/// a key. A rebuilt or older install must not satisfy the rule of a newer stage.
/// </summary>
public sealed class VersionDetectionDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-verdet-" + Guid.NewGuid().ToString("N"));

    public VersionDetectionDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly string[] AllEighteen =
    [
        "aimp", "anaconda", "androidstudio", "awsssmplugin", "azurestorageexplorer", "cpuz",
        "cutepdfwriter", "cyberduck", "daxstudio", "greenshot", "hwmonitor", "pdfstudioviewer",
        "pgadmin4", "positron", "pspad", "rainmeter", "sysinternals", "vim"
    ];

    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static StageDetection Stage(string file, string version, StageTokens? tokens = null)
    {
        var def = DefinitionFixture.Load(file + ".json");
        return StagingService.BuildDetection(def, null, version, tokens: tokens ?? new StageTokens { Version = version });
    }

    [Theory]
    [MemberData(nameof(All))]
    public void NoPackagerDetectsByExistenceAlone(string id)
    {
        var det = DefinitionFixture.Load(id + ".json").Detection;
        Assert.False(det.PropertyType?.Equals("Existence", StringComparison.OrdinalIgnoreCase) == true,
            $"{id} detects a file by existence.");
        Assert.False(det.Type.Equals("RegistryKey", StringComparison.OrdinalIgnoreCase),
            $"{id} detects a key by existence.");
        foreach (var clause in det.Clauses ?? [])
            Assert.False(clause.PropertyType?.Equals("Existence", StringComparison.OrdinalIgnoreCase) == true,
                $"{id} has a clause that detects by existence.");
    }

    public static IEnumerable<object[]> All() => AllEighteen.Select(id => new object[] { id });

    [Theory]
    [InlineData("azurestorageexplorer", @"C:\Program Files\Microsoft Azure Storage Explorer", "StorageExplorer.exe")]
    [InlineData("cyberduck", @"C:\Program Files\Cyberduck", "Cyberduck.exe")]
    [InlineData("daxstudio", @"C:\Program Files\DAX Studio", "DaxStudio.exe")]
    [InlineData("greenshot", @"C:\Program Files\Greenshot", "Greenshot.exe")]
    [InlineData("pdfstudioviewer", @"C:\Program Files\PDFStudioViewer2024", "PDFStudioViewer2024.exe")]
    [InlineData("pspad", @"C:\Program Files\PSPad", "PSPad.exe")]
    [InlineData("rainmeter", @"C:\Program Files\Rainmeter", "Rainmeter.exe")]
    public void AFileDetectionComparesTheFileVersionWithTheStagedVersion(string id, string path, string name)
    {
        var det = Stage(id, "4.5.28");
        Assert.Equal("File", det.Type);
        Assert.Equal(path, det.FilePath);
        Assert.Equal(name, det.FileName);
        Assert.Equal("Version", det.PropertyType);
        Assert.Equal("GreaterEquals", det.Operator);
        Assert.Equal("4.5.28", det.ExpectedValue);
    }

    [Fact]
    public void PositronDropsThePackagingSuffixFromTheFileVersion()
    {
        var det = Stage("positron", "2026.10.1-2");
        Assert.Equal("File", det.Type);
        Assert.Equal("Positron.exe", det.FileName);
        Assert.Equal("Version", det.PropertyType);
        Assert.Equal("GreaterEquals", det.Operator);
        Assert.Equal("2026.10.1", det.ExpectedValue);
    }

    [Theory]
    [InlineData("aimp", "AIMP")]
    [InlineData("cpuz", "CPUID CPU-Z_is1")]
    [InlineData("hwmonitor", "CPUID HWMonitor_is1")]
    [InlineData("vim", "Vim")]
    public void AnUninstallEntryDetectionComparesTheDisplayVersion(string id, string key)
    {
        var det = Stage(id, "9.2.1229");
        Assert.Equal("RegistryKeyValue", det.Type);
        Assert.Equal(Uninstall + key, det.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", det.ValueName);
        Assert.Equal("Version", det.PropertyType);
        Assert.Equal("GreaterEquals", det.Operator);
        Assert.Equal("9.2.1229", det.ExpectedValue);
        Assert.True(det.Is64Bit);
        Assert.Equal("LocalMachine", det.Hive);
    }

    [Fact]
    public void PgAdminNamesItsUninstallKeyAfterTheMajorVersion()
    {
        var det = Stage("pgadmin4", "9.18");
        Assert.Equal(Uninstall + "pgAdmin 4v9_is1", det.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", det.ValueName);
        Assert.Equal("GreaterEquals", det.Operator);
        Assert.Equal("9.18", det.ExpectedValue);
        Assert.True(det.Is64Bit);
    }

    [Fact]
    public void TheSessionManagerPluginIsDetectedByItsReleaseBundleKeyInTheThirtyTwoBitView()
    {
        var det = Stage("awsssmplugin", "1.2.896.0", new StageTokens
        {
            Version = "1.2.896.0",
            BundleId = "{cef0d74c-33bc-432b-941f-6e17bea1f9d3}"
        });
        Assert.Equal("RegistryKeyValue", det.Type);
        Assert.Equal(Uninstall + "{cef0d74c-33bc-432b-941f-6e17bea1f9d3}", det.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", det.ValueName);
        Assert.Equal("GreaterEquals", det.Operator);
        Assert.Equal("1.2.896.0", det.ExpectedValue);
        Assert.False(det.Is64Bit);
    }

    [Fact]
    public void TheBundleIdComesFromTheStagedInstallerHeader()
    {
        var def = DefinitionFixture.Load("awsssmplugin.json");
        var path = Path.Combine(_dir, "SessionManagerPluginSetup.exe");
        var id = Guid.Parse("CEF0D74C-33BC-432B-941F-6E17BEA1F9D3");
        File.WriteAllBytes(path, ArpDetectionKeyAssertionTests.BurnBundle(id));

        var tokens = StagingService.BuildStageTokens(def, path, "1.2.896.0", null, new Dictionary<string, string>());
        var det = StagingService.BuildDetection(def, null, "1.2.896.0", tokens: tokens);

        Assert.Equal(Uninstall + "{CEF0D74C-33BC-432B-941F-6E17BEA1F9D3}", det.RegistryKeyRelative, ignoreCase: true);
    }

    [Fact]
    public void ABundleTokenWithoutABundleRefusesTheStage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new StageTokens().Apply(Uninstall + "{BundleId}"));
        Assert.Contains("{BundleId}", ex.Message);
        Assert.Equal(Uninstall + "{abc}", new StageTokens { BundleId = "{abc}" }.Apply(Uninstall + "{BundleId}"));
    }

    [Theory]
    [InlineData("2026.10.1-2", "2026.10.1")]
    [InlineData("4.5.28", "4.5.28")]
    [InlineData("2026.10.1-rc.1", "2026.10.1")]
    public void TheNumericVersionTokenKeepsTheDottedNumberAndDropsTheSuffix(string version, string expected) =>
        Assert.Equal(expected, StageTokens.ApplyVersionParts("{version_numeric}", version));

    [Fact]
    public void SysinternalsDetectsTheVersionTheInstallWrites()
    {
        var def = DefinitionFixture.Load("sysinternals.json");
        var det = Stage("sysinternals", "2026.10.15");
        Assert.Equal("RegistryKeyValue", det.Type);
        Assert.Equal(@"SOFTWARE\App Packager\Sysinternals Suite", det.RegistryKeyRelative);
        Assert.Equal("Version", det.ValueName);
        Assert.Equal("Version", det.PropertyType);
        Assert.Equal("GreaterEquals", det.Operator);
        Assert.Equal("2026.10.15", det.ExpectedValue);
        Assert.True(det.Is64Bit);

        Assert.NotNull(def.ZipPayload);
        Assert.Equal(@"SOFTWARE\App Packager\Sysinternals Suite", def.ZipPayload!.VersionRegistryKey);
    }

    [Fact]
    public void ASysinternalsStyleZipExtractsFlatAndWritesAndRemovesTheVersionMarker()
    {
        var config = new ZipPayloadConfig
        {
            InstallDir = @"C:\Program Files\Sysinternals",
            FlatExtract = true,
            RequiredEntries = ["procmon.exe"],
            VersionRegistryKey = @"SOFTWARE\App Packager\Sysinternals Suite"
        };
        var zip = Path.Combine(_dir, "SysinternalsSuite.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "procmon.exe", "PsExec.exe", "Eula.txt" })
                using (var s = archive.CreateEntry(name).Open()) s.Write("x"u8);
        }

        Assert.Empty(ZipPayload.Inspect(zip, config));

        var wrappers = new WrapperGeneratorService();
        ZipPayload.GenerateWrappers(wrappers, _dir, "SysinternalsSuite.zip", config, "2026.10.15");
        var install = File.ReadAllText(Path.Combine(_dir, "install.ps1"));
        var uninstall = File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));

        Assert.Contains("ExtractToDirectory($zipPath, $installDir)", install);
        Assert.Contains(@"$installDir = 'C:\Program Files\Sysinternals'", install);
        Assert.Contains(@"New-ItemProperty -Path 'HKLM:\SOFTWARE\App Packager\Sysinternals Suite' -Name Version -Value '2026.10.15'", install);
        Assert.Contains("-ErrorAction Stop", install);
        Assert.Contains(@"Remove-Item -LiteralPath 'HKLM:\SOFTWARE\App Packager\Sysinternals Suite' -Recurse -Force", uninstall);
        Assert.Contains("Remove-Item -LiteralPath $installDir -Recurse -Force", uninstall);
    }

    [Fact]
    public void AFlatZipWithoutItsRequiredEntryRefusesTheStage()
    {
        var zip = Path.Combine(_dir, "empty.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var s = archive.CreateEntry("other.exe").Open()) s.Write("x"u8);
        var config = new ZipPayloadConfig { InstallDir = @"C:\Program Files\Sysinternals", FlatExtract = true, RequiredEntries = ["procmon.exe"] };
        Assert.Throws<InvalidOperationException>(() => ZipPayload.Inspect(zip, config));
    }

    [Fact]
    public void CutePdfNeedsTheUninstallEntryAndTheVersionTheInstallWrote()
    {
        var def = DefinitionFixture.Load("cutepdfwriter.json");
        var det = Stage("cutepdfwriter", "4.0.1.5");
        Assert.Equal("Compound", det.Type);
        Assert.Equal("And", det.Connector);
        Assert.Equal(2, det.Clauses!.Length);
        Assert.Equal("RegistryKey", det.Clauses[0].Type);
        Assert.Equal(Uninstall + "CutePDF Writer Installation", det.Clauses[0].RegistryKeyRelative);
        Assert.Equal("RegistryKeyValue", det.Clauses[1].Type);
        Assert.Equal(@"SOFTWARE\App Packager\CutePDF Writer", det.Clauses[1].RegistryKeyRelative);
        Assert.Equal("Version", det.Clauses[1].ValueName);
        Assert.Equal("Version", det.Clauses[1].PropertyType);
        Assert.Equal("GreaterEquals", det.Clauses[1].Operator);
        Assert.Equal("4.0.1.5", det.Clauses[1].ExpectedValue);

        var install = StagingService.ResolvePostInstallScript(def, null, "4.0.1.5")!;
        Assert.Contains(@"New-ItemProperty -Path 'HKLM:\SOFTWARE\App Packager\CutePDF Writer' -Name Version -Value '4.0.1.5'", install);
        Assert.Contains("-ErrorAction Stop", install);
        Assert.Contains(@"Remove-Item -LiteralPath 'HKLM:\SOFTWARE\App Packager\CutePDF Writer'", def.UninstallCommand);
    }

    [Fact]
    public void AnacondaDetectsByScriptOnYearMonthAndBuild()
    {
        var det = Stage("anaconda", "2026.07-1");
        Assert.Equal("Script", det.Type);
        Assert.Equal("PowerShell", det.ScriptLanguage);
        Assert.Contains("'2026.07-1'", det.ScriptText);
        Assert.Contains("Registry64", det.ScriptText);
        Assert.Contains("StartsWith('Anaconda3 '", det.ScriptText);
        Assert.Contains("Write-Output 'Installed'", det.ScriptText);
        Assert.DoesNotContain("{version}", det.ScriptText);
    }

    [Fact]
    public void AnacondaDetectionScriptComparesTheThreeNumbersNotTheText()
    {
        var script = Stage("anaconda", "2026.07-1").ScriptText!;
        // 2026.10-1 is newer than 2026.07-2 although the text "10" sorts before "7".
        Assert.Contains("[int]$m.Groups[2].Value", script);
        Assert.Contains("-ge $minimum", script);
    }

    [Fact]
    public void AndroidStudioDetectsTheBuildNumberInProductInfo()
    {
        var det = Stage("androidstudio", "2026.2.2.8", new StageTokens { Version = "2026.2.2.8", FeedBuild = "262.10968.63.2622.16730706" });
        Assert.Equal("Script", det.Type);
        Assert.Contains("product-info.json", det.ScriptText);
        Assert.Contains("buildNumber", det.ScriptText);
        Assert.Contains("$need = @('262.10968.63.2622.16730706'.Split('.')", det.ScriptText);
        Assert.Contains("Write-Output 'Installed'", det.ScriptText);
        Assert.DoesNotContain("{FeedBuild}", det.ScriptText);
    }

    [Fact]
    public void AMissingFeedBuildRefusesTheAndroidStudioStage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new StageTokens().Apply("@({FeedBuild})"));
        Assert.Contains("{FeedBuild}", ex.Message);
    }

    [Fact]
    public void TheFeedBuildComesFromTheReleaseEntryOfTheScrapedVersion()
    {
        const string feed = """
            {"content":{"item":[
             {"build":"AI-262.10968.63.2622.16840690","platformVersion":"2026.2.3","name":"Android Studio Rabbit 2 | 2026.2.2 Patch 1","channel":"Patch","version":"2026.2.2.9"},
             {"build":"AI-262.10968.63.2622.16730706","platformVersion":"2026.2.3","name":"Android Studio Rabbit 2 | 2026.2.2","channel":"Release","version":"2026.2.2.8"},
             {"build":"AI-262.9437.185.2621.16467767","platformVersion":"2026.2.2","name":"Android Studio Rabbit 1 | 2026.2.1","channel":"Release","version":"2026.2.1.8"}
            ]}}
            """;
        var config = DefinitionFixture.Load("androidstudio.json").VersionScraper;
        Assert.False(string.IsNullOrWhiteSpace(config.BuildQuery));

        Assert.Equal("262.10968.63.2622.16730706", StagingService.ExtractFeedBuild(feed, config, "2026.2.2.8"));
        Assert.Equal("262.9437.185.2621.16467767", StagingService.ExtractFeedBuild(feed, config, "2026.2.1.8"));
        Assert.Null(StagingService.ExtractFeedBuild(feed, config, "2026.2.2.9"));
        Assert.Null(StagingService.ExtractFeedBuild(feed, config, "2026.2.2"));
    }

    [Theory]
    [MemberData(nameof(All))]
    public void EveryDefinitionStillLoads(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Definitions", id + ".json")));
        Assert.True(doc.RootElement.TryGetProperty("Detection", out _));
    }
}
