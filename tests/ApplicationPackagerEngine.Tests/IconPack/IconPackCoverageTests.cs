using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.IconPack;

/// <summary>
/// The icon pack (jasonulbright/app-packager-icons, manifest of v2026.11.16.0009 kept in pack-manifest.json) against the
/// shipped definitions. A definition with no icon in the pack fails the test unless it is on the named list below; the list
/// shrinks when a new pack release adds the icons (copy its manifest.json over pack-manifest.json).
/// </summary>
public sealed class IconPackCoverageTests
{
    /// <summary>Shipped definitions that the pack of 2026-11-16 has no icon for. Part B of the icon work adds them to the pack.</summary>
    private static readonly string[] WithoutIcon =
    [
        "anypointstudio", "aspnethostingbundle11", "aspnethostingbundle9", "Dotnet11x64", "Dotnet9x86", "filezilla",
        "foxitreader", "powershell7lts", "vmwaretools", "vs2026", "vs2026community", "wiztree"
    ];

    private static string[] ShippedBaseNames() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.OrdinalIgnoreCase).ToArray()!;

    private static IconPackManifest Manifest()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return IconPackService.ReadManifest(Path.Combine(folder.FullName, "tests", "ApplicationPackagerEngine.Tests", "IconPack", "pack-manifest.json"))
               ?? throw new InvalidOperationException("pack-manifest.json is not readable.");
    }

    [Fact]
    public void EveryShippedDefinitionHasAnIconInThePackOrIsOnTheNamedList()
    {
        var names = ShippedBaseNames();
        var covered = IconPackService.MapEntries(Manifest().Icons, names).Mappings
            .Select(m => m.DefinitionBaseName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = names.Where(n => !covered.Contains(n)).ToArray();

        var unexpected = missing.Except(WithoutIcon, StringComparer.OrdinalIgnoreCase).ToArray();
        var fixedNow = WithoutIcon.Except(missing, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(unexpected.Length == 0, "No icon in the pack, and not on the list: " + string.Join(", ", unexpected));
        Assert.True(fixedNow.Length == 0, "On the list but the pack has an icon now (remove from the list): " + string.Join(", ", fixedNow));
    }

    [Theory]
    [InlineData("2026.11.16.0009", "v2026.11.16.0009", true)]
    [InlineData("v2026.11.16.0009", "2026.11.16.0009", true)]
    [InlineData("2026.09.29.0008", "v2026.11.16.0009", false)]
    [InlineData("", "v2026.11.16.0009", false)]
    [InlineData("2026.11.16.0009", "", false)]
    public void AReleaseTagIsCurrentOnlyForTheSameVersion(string installed, string tag, bool expected) =>
        Assert.Equal(expected, IconPackService.IsCurrent(new IconPackStatus(true, installed, 10), tag));

    [Fact]
    public void NoInstalledPackIsNeverCurrent() =>
        Assert.False(IconPackService.IsCurrent(new IconPackStatus(false, "2026.11.16.0009", 0), "v2026.11.16.0009"));
}
