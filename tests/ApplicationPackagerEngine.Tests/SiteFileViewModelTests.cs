using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The site-file import window model and the footer menu of Q-112h: targets, preview, merge, new site, icons on the entries.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteFileViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-sitefilevm-" + Guid.NewGuid().ToString("N"));

    public SiteFileViewModelTests()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", "Production", _root);
        File.WriteAllText(Path.Combine(SiteDataPaths.GetSiteFolder("AB1", dataRoot: _root), "settings.json"),
            """{ "ContentDistribution": { "DpGroupName": "AB1 DPs" }, "SiteHygiene": { "PauseDays": 3 }, "GitHubToken": "ghp_PLANTED" }""", new UTF8Encoding(false));
    }

    public void Dispose() => TestCleanup.DeleteDirectory(_root);

    private string ExportFile()
    {
        var path = Path.Combine(_root, "out.zip");
        var result = new SiteManager(() => null, _root).ExportSiteFile("AB1", path);
        Assert.True(result.Ok, result.Reason);
        return path;
    }

    [Fact]
    public void AFileOfAnExistingCodeOffersMergeAndANewEnvironmentAndPreselectsTheSiteWithThatCode()
    {
        var content = SiteFile.Read(ExportFile()).Content!;
        var vm = new ImportSiteSettingsViewModel(content, "AB1", _root);
        Assert.True(vm.IsFileMode);
        Assert.Equal(["AB1", "AB1~2"], vm.Targets.Select(t => t.FolderName).ToArray());
        Assert.False(vm.SelectedTarget!.IsNew);
        Assert.Contains("Site file: AB1 - Production", vm.SourceText);
        Assert.Equal("Copy from (site file)", vm.SourceLabel);
    }

    [Fact]
    public void MergeKeepsExistingValuesAndNewSiteIsCreatedOnlyOnImport()
    {
        var content = SiteFile.Read(ExportFile()).Content!;
        var vm = new ImportSiteSettingsViewModel(content, "AB1", _root);
        Assert.False(vm.CanImport); // the same values: nothing to copy
        vm.SelectedTarget = vm.Targets.Single(t => t.IsNew);
        Assert.True(vm.CanImport);
        Assert.False(Directory.Exists(SiteDataPaths.GetSiteFolder("AB1~2", dataRoot: _root)));
        vm.ImportCommand.Execute(null);
        Assert.NotNull(vm.Result);
        Assert.True(Directory.Exists(SiteDataPaths.GetSiteFolder("AB1~2", dataRoot: _root)));
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(SiteDataPaths.GetSiteFolder("AB1~2", dataRoot: _root), "settings.json")))!;
        Assert.Equal("AB1 DPs", settings["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.Null(settings["GitHubToken"]);
        Assert.Contains("Imported the site file of AB1 into site AB1~2", ImportSiteSettingsViewModel.ResultMessage(vm.Result!, "AB1~2", fromFile: true));
    }

    [Fact]
    public void AFileOfAnUnknownCodeOffersTheNewSiteFirst()
    {
        var path = ExportFile();
        var other = Path.Combine(_root, "other");
        SiteCatalog.Create("CD2", "cm.cd2.example.com", "Other", other);
        var content = SiteFile.Read(path).Content!;
        var vm = new ImportSiteSettingsViewModel(content, "CD2", other);
        Assert.True(vm.SelectedTarget!.IsNew);
        Assert.Equal("AB1", vm.SelectedTarget.FolderName);
    }

    [Fact]
    public void TheExportAndImportEntriesHaveIconsAndAutomationIds()
    {
        var source = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Windows", "MainWindow.xaml.cs"));
        Assert.Contains("Header = \"Export site file...\", Icon = new SymbolIcon(SymbolRegular.DocumentArrowRight24)", source);
        Assert.Contains("Header = \"Import site file...\", Icon = new SymbolIcon(SymbolRegular.DocumentArrowLeft24)", source);
        Assert.Contains("miExportSiteFile", source);
        Assert.Contains("miImportSiteFile", source);
    }

    [Fact]
    public void ExportIsRefusedWhenNoSiteIsActive()
    {
        var saved = SiteContext.ActiveFolderName;
        try
        {
            SiteContext.Deactivate();
            Assert.NotNull(new SiteManager(() => null, _root).ExportRefusal());
        }
        finally
        {
            if (saved is not null) SiteContext.Activate(saved, _root);
        }
    }
}
