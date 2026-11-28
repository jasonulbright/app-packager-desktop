using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.IconPack;

/// <summary>The Packages grid shows the icon that the staging step would take: one lookup for both.</summary>
public sealed class PackagesGridIconTests
{
    [Fact]
    public void TheGridLookupEqualsTheStagingLookupForADefinitionFileWithALocalIcon()
    {
        var root = Directory.CreateTempSubdirectory("apd-icon-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Icons"));
            var definition = Path.Combine(root, "tool.json");
            File.WriteAllText(definition, "{}");
            File.WriteAllBytes(Path.Combine(root, "Icons", "tool.png"), [0x89, 0x50, 0x4E, 0x47]);

            var staging = IconExtractionService.FindExternalIconPath(new AppDefinition { SourcePath = definition });
            var grid = IconExtractionService.FindExternalIconPathForFile(definition);
            Assert.NotNull(grid);
            Assert.Equal(staging, grid);
            Assert.EndsWith("tool.png", grid);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ADefinitionWithoutAnIconAndABlankPathGiveNone()
    {
        var root = Directory.CreateTempSubdirectory("apd-icon-").FullName;
        try
        {
            var definition = Path.Combine(root, "noicon-zz-test.json");
            File.WriteAllText(definition, "{}");
            Assert.Null(IconExtractionService.FindExternalIconPathForFile(definition));
            Assert.Null(IconExtractionService.FindExternalIconPathForFile(" "));
            Assert.Null(IconExtractionService.FindExternalIconPathForFile(null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EveryShippedDefinitionFileResolvesThroughTheSameLookupAsStaging()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json"))
            Assert.Equal(IconExtractionService.FindExternalIconPath(new AppDefinition { SourcePath = file }),
                IconExtractionService.FindExternalIconPathForFile(file));
    }

    [Fact]
    public void RefreshIconTellsTheGridThatTheIconChanged()
    {
        var row = new ApplicationPackagerUI.Models.PackagerInfo();
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        row.RefreshIcon();
        Assert.Contains("IconPath", changed);
        Assert.Contains("IconUri", changed);
        Assert.Contains("IconTooltip", changed);
    }

    // The icon of a row comes from the row item itself (its definition file), never from a position, so a sort, a filter or a refresh
    // cannot give a row the icon of another application.
    [Fact]
    public void EveryRowKeepsTheIconOfItsOwnDefinitionAfterEachSortOfTheView()
    {
        var root = Directory.CreateTempSubdirectory("apd-icon-sort-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Icons"));
            var names = new[] { "office", "odbc", "zoom", "putty", "git", "vlc" };
            var rows = new System.Collections.ObjectModel.ObservableCollection<ApplicationPackagerUI.Models.PackagerInfo>();
            var i = 0;
            foreach (var n in names)
            {
                var definition = Path.Combine(root, n + ".json");
                File.WriteAllText(definition, "{}");
                File.WriteAllBytes(Path.Combine(root, "Icons", n + ".png"), [0x89, 0x50, 0x4E, 0x47, (byte)i++]);
                rows.Add(new ApplicationPackagerUI.Models.PackagerInfo { IsJsonDefined = true, DefinitionPath = definition, Application = names[(i + 2) % names.Length], Vendor = names[(i * 5) % names.Length] });
            }

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(rows);
            foreach (var column in new[] { nameof(ApplicationPackagerUI.Models.PackagerInfo.Application), nameof(ApplicationPackagerUI.Models.PackagerInfo.Vendor) })
            foreach (var direction in new[] { System.ComponentModel.ListSortDirection.Ascending, System.ComponentModel.ListSortDirection.Descending })
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(column, direction));
                foreach (ApplicationPackagerUI.Models.PackagerInfo row in view)
                    Assert.Equal(Path.GetFileNameWithoutExtension(row.DefinitionPath), Path.GetFileNameWithoutExtension(row.IconPath));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheGridBindsTheIconToTheRowItemAndNotToAPosition()
    {
        var xaml = File.ReadAllText(FindRepoFile("src/ApplicationPackagerUI/Views/Pages/PackagesPage.xaml"));
        Assert.Contains("Source=\"{Binding IconImage,", xaml);
        // The alternation index only moves the mark and the status text by a unit; it never picks the picture.
        Assert.DoesNotMatch("(Source|Text|IconImage|IconPath)=\"[^\"]*AlternationIndex", xaml);
        Assert.DoesNotContain("IconIndex", xaml);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
}
