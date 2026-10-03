using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// An icon pack entry named after the definition file wins at stage for
/// every IconSource value. Without one, Installer extracts and None stages
/// nothing. At install, an entry that names a definition exactly keeps its
/// icon even when an arch-collapsed entry fans out to the same definition.
/// </summary>
public sealed class IconPackPrecedenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-iconpack-" + Guid.NewGuid().ToString("N"));

    public IconPackPrecedenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppDefinition Definition(string name, string? iconSource)
    {
        var path = Path.Combine(_dir, "Definitions", name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        return new AppDefinition { Id = "abc123", IconSource = iconSource, SourcePath = path };
    }

    private string Stage(string name)
    {
        var stage = Path.Combine(_dir, name);
        Directory.CreateDirectory(stage);
        return stage;
    }

    [Theory]
    [InlineData("Installer")]
    [InlineData("External")]
    [InlineData("None")]
    [InlineData("")]
    [InlineData(null)]
    public void ThePackIconWinsForEveryTag(string? tag)
    {
        var def = Definition("widget", tag);
        var icons = IconExtractionService.ExternalIconsDirectory(def);
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(icons, "widget.png"), "pack");
        var stage = Stage("stage-" + (tag ?? "null"));
        File.WriteAllText(Path.Combine(stage, "widget-setup.exe"), "x");
        var manifest = new StageManifest { InstallerFile = "widget-setup.exe" };
        var logs = new List<string>();

        IconExtractionService.StageIcon(def, stage, manifest, logs.Add);

        Assert.Equal("app-icon.png", manifest.Icon);
        Assert.Equal("pack", File.ReadAllText(Path.Combine(stage, "app-icon.png")));
        Assert.DoesNotContain(logs, l => l.Contains("not in the stage folder") || l.Contains("Icon staging failed"));
    }

    [Fact]
    public void AnIcoEntryWinsOverAPngEntry()
    {
        var def = Definition("widget", "None");
        var icons = IconExtractionService.ExternalIconsDirectory(def);
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(icons, "widget.png"), "png");
        File.WriteAllText(Path.Combine(icons, "widget.ico"), "ico");
        var manifest = new StageManifest();

        IconExtractionService.StageIcon(def, Stage("stage-ico"), manifest);

        Assert.Equal("app-icon.ico", manifest.Icon);
    }

    [Fact]
    public void WithoutAPackEntryInstallerExtractsFromThePayload()
    {
        var def = Definition("widget", "Installer");
        var stage = Stage("stage-extract");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Path.Combine(stage, "widget-setup.exe"));
        var manifest = new StageManifest { InstallerFile = "widget-setup.exe" };

        IconExtractionService.StageIcon(def, stage, manifest);

        Assert.Equal("app-icon.ico", manifest.Icon);
    }

    [Fact]
    public void WithoutAPackEntryNoneStagesNothing()
    {
        var stage = Stage("stage-none");
        var manifest = new StageManifest { InstallerFile = "widget-setup.exe" };

        IconExtractionService.StageIcon(Definition("widget", "None"), stage, manifest);

        Assert.Null(manifest.Icon);
        Assert.Empty(Directory.GetFiles(stage, "app-icon.*"));
    }

    [Fact]
    public void APackEntryForALongerNameThatSharesThePrefixIsIgnored()
    {
        var def = Definition("chrome", "None");
        var icons = IconExtractionService.ExternalIconsDirectory(def);
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(icons, "chrome.remote.png"), "pack");
        var manifest = new StageManifest();

        IconExtractionService.StageIcon(def, Stage("stage-prefix"), manifest);

        Assert.Null(manifest.Icon);
    }

    [Theory]
    [InlineData("msvcruntimes,msvcruntimesx64,msvcruntimesx86")]
    [InlineData("msvcruntimesx86,msvcruntimesx64,msvcruntimes")]
    public void AnExactEntryKeepsItsDefinitionWhateverTheManifestOrder(string order)
    {
        var entries = order.Split(',').Select(p => new IconPackEntry { Packager = p, File = p + ".png" });

        var map = IconPackService.MapEntries(entries, ["msvcruntimesx64", "msvcruntimesx86"]);

        Assert.Equal(2, map.Mappings.Count);
        Assert.All(map.Mappings, m => Assert.True(m.Exact));
        Assert.Single(map.Mappings, m => m.Entry.Packager == "msvcruntimesx64" && m.DefinitionBaseName == "msvcruntimesx64");
        Assert.Single(map.Mappings, m => m.Entry.Packager == "msvcruntimesx86" && m.DefinitionBaseName == "msvcruntimesx86");
    }

    [Fact]
    public void TheNewDefinitionsCarryTheIconPackNamesExactly()
    {
        var names = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);

        foreach (var packager in new[]
                 {
                     "apppackagersuite", "aspnethostingbundle10", "beyondcompare5", "citrixworkspace-ltsr-arm64",
                     "citrixworkspace-ltsr-x64", "citrixworkspace-ltsr-x86", "githubdesktop", "jabradirect",
                     "ms-openjdk17-exe", "ms-openjdk17-exe-user", "ms-openjdk17-msi", "ms-openjdk17-msi-user",
                     "ms-openjdk21-exe", "ms-openjdk21-exe-user", "ms-openjdk21-msi", "ms-openjdk21-msi-user",
                     "ms-openjdk25-exe", "ms-openjdk25-exe-user", "ms-openjdk25-msi", "ms-openjdk25-msi-user",
                     "msvcruntimesx64", "msvcruntimesx86", "rtools", "signingsuite", "slido",
                     "tableaudesktop", "tableauprep", "tableaureader", "anypointstudio"
                 })
            Assert.Contains(packager, names);

        Assert.DoesNotContain("msvcruntimes-x64", names);
        Assert.DoesNotContain("msvcruntimes-x86", names);
    }
}
