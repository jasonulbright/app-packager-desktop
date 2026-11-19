using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// TeamViewer Host and TortoiseSVN have no download a script can use. Both stage the MSI that the operator puts in the
/// installer source folder: the version is the ProductVersion of the file, no version request is sent, a missing file is
/// refused with a message that names the folder and the file to put there, and Check Latest says that it has no web source.
/// </summary>
public sealed class InstallerSourceFolderTests : IDisposable
{
    private const string TeamViewerFile = "teamviewerhost.json";
    private const string TortoiseFile = "tortoisesvn.json";
    private const string ProductCode = "{8A6F0B37-3C1D-4E5A-9B2E-7D4C1F0A5E21}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-sourcefolder-" + Guid.NewGuid().ToString("N"));

    public InstallerSourceFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Msi(string folder, string fileName, string version)
    {
        var path = Path.Combine(folder, fileName);
        using (new TestMsiBuilder(path)
            .Property("ProductName", "Widget")
            .Property("ProductVersion", version)
            .Property("ProductCode", ProductCode)
            .Property("Manufacturer", "Contoso")) { }
        return path;
    }

    /// <summary>No scraper, no download: a request to the web would fail the test.</summary>
    private static StagingService Staging() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());

    // ---- the two definitions ----

    [Theory]
    [InlineData(TeamViewerFile, "TeamViewer_Host*.msi", true)]
    [InlineData(TortoiseFile, "TortoiseSVN-*-x64-*.msi", false)]
    public void BothDefinitionsStageAnMsiFromTheFolderAndHaveNoWebVersionSource(string file, string filter, bool requireSignature)
    {
        var def = DefinitionFixture.Load(file);

        Assert.Equal("MSI", def.InstallerType);
        Assert.Equal("LocalFile", def.VersionScraper.Type);
        Assert.Equal("", def.DownloadUrl);
        Assert.NotNull(def.LocalSource);
        Assert.True(def.LocalSource!.IsRequired);
        Assert.True(def.LocalSource.VersionFromFile);
        Assert.Equal(filter, def.LocalSource.Filter);
        Assert.Equal(requireSignature, def.LocalSource.RequireSignature);
        Assert.False(string.IsNullOrWhiteSpace(def.LocalSource.Note));
        Assert.NotNull(new ScraperRegistry().Get(def.VersionScraper.Type));
    }

    [Theory]
    [InlineData(TeamViewerFile, "TeamViewer_Host.msi", true)]
    [InlineData(TeamViewerFile, "TeamViewer_Host (1).msi", true)]
    [InlineData(TeamViewerFile, "TeamViewer_Host_Setup_x64.exe", false)]
    [InlineData(TeamViewerFile, "TeamViewer_Host.zip", false)]
    [InlineData(TortoiseFile, "TortoiseSVN-1.14.9.29743-x64-svn-1.14.5.msi", true)]
    [InlineData(TortoiseFile, "TortoiseSVN-1.14.9.29743-win32-svn-1.14.5.msi", false)]
    [InlineData(TortoiseFile, "TortoiseSVN-1.14.9.29743-x64-svn-1.14.5.zip", false)]
    public void TheFileNameRuleMatchesTheVendorNames(string file, string name, bool matches)
    {
        var filter = DefinitionFixture.Load(file).LocalSource!.Filter;
        Assert.Equal(matches, LocalSourceResolver.WildcardMatch(name, filter));
    }

    [Fact]
    public void TheDefinitionsNameTheFolderAndTheDroppedFileInTheirNotes()
    {
        Assert.Contains("TeamViewer_Host.msi", DefinitionFixture.Load(TeamViewerFile).LocalSource!.Note);
        Assert.Contains("Management Console", DefinitionFixture.Load(TeamViewerFile).LocalSource!.Note);
        Assert.Contains("TortoiseSVN-<version>-x64-svn-<svn version>.msi", DefinitionFixture.Load(TortoiseFile).LocalSource!.Note);
        Assert.Contains("does not download", DefinitionFixture.Load(TortoiseFile).LocalSource!.Note);
    }

    [Fact]
    public void TheTortoiseDefinitionCarriesNoUrlOfTheProtectedSite()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Definitions", TortoiseFile));
        Assert.DoesNotContain("tortoisesvn.net/downloads", text);
        Assert.DoesNotContain("sourceforge", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the TeamViewer Host options become MSI properties ----

    [Fact]
    public void EmptyTeamViewerSettingsAddNoProperty()
    {
        var def = DefinitionFixture.Load(TeamViewerFile);
        Assert.Equal("/qn /norestart", StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, new()));
    }

    [Fact]
    public void TeamViewerSettingsBecomeMsiProperties()
    {
        var def = DefinitionFixture.Load(TeamViewerFile);
        var args = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, new()
        {
            ["ApiToken"] = "tok-123",
            ["CustomConfigId"] = "cfg456",
            ["AssignmentId"] = "0001-aaaa",
            ["AssignmentOptions"] = "--grant-easy-access --reassign",
            ["RemoveDesktopShortcut"] = "true",
        });

        Assert.StartsWith("/qn /norestart", args);
        Assert.Contains("APITOKEN=tok-123", args);
        Assert.Contains("CUSTOMCONFIGID=cfg456", args);
        Assert.Contains("ASSIGNMENTID=0001-aaaa", args);
        Assert.Contains("ASSIGNMENTOPTIONS=\"--grant-easy-access --reassign\"", args);
        Assert.Contains("DESKTOPSHORTCUTS=0", args);
    }

    [Fact]
    public void ASpaceInATokenIsRefused()
    {
        var def = DefinitionFixture.Load(TeamViewerFile);
        Assert.Throws<InvalidOperationException>(() => StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, new() { ["ApiToken"] = "a b" }));
    }

    [Fact]
    public async Task NoWebSourceIsATypedRefusalSoACveRunCanTreatItAsNoVendorVersion()
    {
        var ex = await Assert.ThrowsAsync<NoWebVersionSourceException>(() => Staging().GetLatestVersionAsync(DefinitionFixture.Load(TortoiseFile)));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void TheTeamViewerDetectionReadsTheAddRemoveProgramsEntryInBothViews()
    {
        var det = DefinitionFixture.Load(TeamViewerFile).Detection;
        Assert.Equal("ArpEntry", det.Type);
        Assert.Equal("Both", det.ArpEntry!.View);
        Assert.True(det.ArpEntry.WindowsInstaller);
        Assert.Equal("{version}", det.ArpEntry.Version);
    }

    // ---- the folder and the file ----

    [Fact]
    public void EveryRefusalAboutTheFolderEndsWithWhatToPutThere()
    {
        var config = DefinitionFixture.Load(TeamViewerFile).LocalSource!;

        var none = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve("", config, "TeamViewer Host (x64)"));
        var missing = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve(Path.Combine(_dir, "missing"), config, "TeamViewer Host (x64)"));
        var empty = Folder("empty");
        var noMatch = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve(empty, config, "TeamViewer Host (x64)"));

        foreach (var ex in new[] { none, missing, noMatch })
            Assert.EndsWith(config.Note!.Trim(), ex.Message);
        Assert.Contains(Path.Combine(_dir, "missing"), missing.Message);
        Assert.Contains(empty, noMatch.Message);
        Assert.Contains("'TeamViewer_Host*.msi'", noMatch.Message);
    }

    [Fact]
    public void AZipInTheFolderIsNamedAndNotRead()
    {
        var folder = Folder("zipped");
        File.WriteAllText(Path.Combine(folder, "TeamViewer_Host.zip"), "x");

        var ex = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve(folder, DefinitionFixture.Load(TeamViewerFile).LocalSource!, "TeamViewer Host (x64)"));

        Assert.Contains("holds a .zip file", ex.Message);
        Assert.Contains("Unzip it", ex.Message);
    }

    [Fact]
    public void AnMsiIsRankedByItsProductVersionNotByItsWriteTime()
    {
        var folder = Folder("ranked");
        var older = Msi(folder, "TortoiseSVN-1.14.8.1-x64-svn-1.14.5.msi", "1.14.8");
        var newer = Msi(folder, "TortoiseSVN-1.14.9.1-x64-svn-1.14.5.msi", "1.14.29743");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow);

        Assert.Equal(newer, LocalSourceResolver.Resolve(folder, DefinitionFixture.Load(TortoiseFile).LocalSource!, "TortoiseSVN (x64)"));
        Assert.Equal("1.14.29743", LocalSourceResolver.ReadVersion(newer));
    }

    // ---- Check Latest ----

    [Fact]
    public async Task CheckLatestReportsTheVersionOfTheMsiInTheFolder()
    {
        var folder = Folder("latest");
        Msi(folder, "TortoiseSVN-1.14.9.29743-x64-svn-1.14.5.msi", "1.14.29743");

        var version = await Staging().GetLatestVersionAsync(DefinitionFixture.Load(TortoiseFile), default, folder);

        Assert.Equal("1.14.29743", version);
    }

    [Theory]
    [InlineData(TeamViewerFile)]
    [InlineData(TortoiseFile)]
    public async Task CheckLatestWithoutAFolderSaysThereIsNoWebSourceAndNamesTheFile(string file)
    {
        var def = DefinitionFixture.Load(file);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Staging().GetLatestVersionAsync(def));

        Assert.Contains("has no web version source", ex.Message);
        Assert.Contains("installer source folder", ex.Message);
        Assert.EndsWith(def.LocalSource!.Note!.Trim(), ex.Message);
    }

    [Fact]
    public async Task CheckLatestWithAnEmptyFolderNamesTheFolder()
    {
        var folder = Folder("nothing");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Staging().GetLatestVersionAsync(DefinitionFixture.Load(TeamViewerFile), default, folder));

        Assert.Contains(folder, ex.Message);
        Assert.Contains("TeamViewer_Host.msi", ex.Message);
    }

    [Fact]
    public async Task TheLocalFileScraperSaysThereIsNoWebSourceInsteadOfReturningNothing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ScraperRegistry().GetLatestVersionAsync(new VersionScraperConfig { Type = "LocalFile" }));

        Assert.Contains("no web version source", ex.Message);
    }

    // ---- Stage ----

    [Fact]
    public async Task StageTakesTheVersionFromTheMsiAndSendsNoRequest()
    {
        var folder = Folder("stage");
        Msi(folder, "TortoiseSVN-1.14.9.29743-x64-svn-1.14.5.msi", "1.14.29743");
        var logs = new List<string>();

        var result = await Staging().StageAsync(DefinitionFixture.Load(TortoiseFile), Folder("root"), logs.Add,
            inputs: new StageInputs { LocalSourceFolder = folder });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("1.14.29743", Path.GetFileName(result.ContentPath));
        Assert.True(File.Exists(Path.Combine(result.ContentPath, "TortoiseSVN-1.14.29743-x64.msi")));
        Assert.DoesNotContain(logs, l => l.StartsWith("Downloading", StringComparison.Ordinal) || l.StartsWith("Checking latest", StringComparison.Ordinal));
        var manifest = new ManifestService().Read(result.ManifestPath);
        Assert.Equal("1.14.29743", manifest.SoftwareVersion);
        Assert.Equal(ProductCode, manifest.ProductCode);
        Assert.Contains(logs, l => l.Contains("WARNING") && l.Contains("staging it as found"));
    }

    [Fact]
    public async Task StageWithoutAFolderNamesWhatToPutThere()
    {
        var def = DefinitionFixture.Load(TeamViewerFile);

        var result = await Staging().StageAsync(def, Folder("root"));

        Assert.False(result.Success);
        Assert.Contains("No installer source folder is set for TeamViewer Host (x64)", result.ErrorMessage);
        Assert.EndsWith(def.LocalSource!.Note!.Trim(), result.ErrorMessage);
    }

    [Fact]
    public async Task TeamViewerHostRefusesAnMsiWithoutAValidSignature()
    {
        var folder = Folder("unsignedtv");
        Msi(folder, "TeamViewer_Host.msi", "15.62.3");

        var result = await Staging().StageAsync(DefinitionFixture.Load(TeamViewerFile), Folder("root"),
            inputs: new StageInputs { LocalSourceFolder = folder });

        Assert.False(result.Success);
        Assert.Contains("Installer signature is", result.ErrorMessage);
        Assert.Contains("not Valid", result.ErrorMessage);
    }

    // ---- Settings ----

    [Fact]
    public void TheSettingsRowTellsTheFileAndTheRule()
    {
        var hint = SettingsViewModel.LocalSourceRowHint(DefinitionFixture.Load(TortoiseFile).LocalSource!);

        Assert.Contains("Required", hint);
        Assert.Contains("TortoiseSVN-*-x64-*.msi", hint);
        Assert.Contains("no web version source", hint);
        Assert.Contains("unsigned file is staged", hint);
        Assert.Contains("TortoiseSVN-<version>-x64-svn-<svn version>.msi", hint);
    }

    [Fact]
    public void TheFolderPromptAddsTheNoteOfTheDefinition()
    {
        var text = ApplicationPackagerUI.Services.LocalSourceFolderPrompt.Description("TeamViewer Host (x64)", "Put TeamViewer_Host.msi here.");

        Assert.Contains("TeamViewer Host (x64)", text);
        Assert.EndsWith("Put TeamViewer_Host.msi here.", text);
    }
}
