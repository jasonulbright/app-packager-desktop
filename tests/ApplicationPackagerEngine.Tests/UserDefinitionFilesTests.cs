using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using System.Runtime.InteropServices;
using System.Text;

namespace ApplicationPackagerEngine.Tests;

[Collection(WorkbenchEnvironmentCollection.Name)]
public sealed class UserDefinitionFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-user-definitions-" + Guid.NewGuid().ToString("N"));

    public UserDefinitionFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SaveNew_SanitizesNamesAndKeepsFilesInsideTheSelectedUserRoot()
    {
        var path = UserDefinitionFiles.SaveNew(_root, "../../Unsafe App", new AppDefinition
        {
            Id = "customunsafe01",
            Application = "Unsafe App"
        });

        var expectedPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedPrefix, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("unsafeapp.json", Path.GetFileName(path));
        Assert.DoesNotContain("SourceOrigin", File.ReadAllText(path));
        Assert.DoesNotContain("SourcePath", File.ReadAllText(path));
    }

    [Fact]
    public void SaveNew_RefusesReservedDeviceNamesAndDoesNotOverwriteExistingDefinitions()
    {
        var path = UserDefinitionFiles.SaveNew(_root, "CON", new AppDefinition
        {
            Id = "first01",
            Vendor = "First"
        });
        var original = File.ReadAllText(path);

        Assert.Equal("app-con.json", Path.GetFileName(path));
        Assert.Throws<IOException>(() => UserDefinitionFiles.SaveNew(_root, "con", new AppDefinition
        {
            Id = "second01",
            Vendor = "Second"
        }));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void SaveNew_RefusesNamesWithoutLettersOrDigits()
    {
        Assert.Throws<InvalidOperationException>(() => UserDefinitionFiles.GetPath(_root, "../"));
    }

    [Fact]
    public void ExplicitDataRootsArePartitionedByWindowsUserAndNeverAllowedUnderProgramFiles()
    {
        var scoped = AppDataPaths.ScopeRootToCurrentUser(_root);
        Assert.Equal(Path.Combine(_root, "Users", System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value), scoped);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Throws<UnauthorizedAccessException>(() => AppDataPaths.ScopeRootToCurrentUser(
            Path.Combine(programFiles, "AppPackagerTestData")));
    }

    [Fact]
    public void ProgramFilesGuardRejectsExtendedDeviceNamespacePaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(string.IsNullOrWhiteSpace(programFiles));

        Assert.Throws<UnauthorizedAccessException>(() => AppDataPaths.EnsureOutsideProgramFiles(
            @"\\?\" + programFiles));
        Assert.Throws<UnauthorizedAccessException>(() => AppDataPaths.EnsureOutsideProgramFiles(
            @"\\.\" + programFiles));
    }

    [Fact]
    public void ProgramFilesGuardRejectsShortNameAliasesWhenAvailable()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) return;

        var buffer = new StringBuilder(32768);
        var length = GetShortPathName(programFiles, buffer, (uint)buffer.Capacity);
        if (length is 0 or >= 32768) return;

        var shortProgramFiles = buffer.ToString();
        if (string.Equals(shortProgramFiles, programFiles, StringComparison.OrdinalIgnoreCase)) return;

        Assert.Throws<UnauthorizedAccessException>(() => AppDataPaths.EnsureOutsideProgramFiles(
            Path.Combine(shortProgramFiles, "AppPackagerTestData")));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

    [Fact]
    public void EffectiveDefinitions_UseUserOverrideForCatalogIdAndCustomIdentityForNewFiles()
    {
        var catalog = Path.Combine(_root, "catalog");
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(catalog);
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(catalog, "shipped.json"),
            """{ "Id": "same01", "Application": "Shipped", "Vendor": "Vendor A" }""");
        var overridePath = Path.Combine(user, "my-shipped.json");
        File.WriteAllText(overridePath,
            """{ "Id": "same01", "Application": "Edited", "Vendor": "Vendor B" }""");
        File.WriteAllText(Path.Combine(user, "mine.json"),
            """{ "Id": "private01", "Application": "Mine" }""");

        var definitions = PackagerDiscoveryService.LoadEffectiveDefinitions(catalog, user);
        var overridden = Assert.Single(definitions, definition => definition.Id == "same01");
        var custom = Assert.Single(definitions, definition => definition.Id == "private01");

        Assert.Equal("Vendor B", overridden.Vendor);
        Assert.Equal(Path.GetFullPath(overridePath), overridden.SourcePath);
        Assert.Equal(ApplicationOrigin.Catalog, overridden.SourceOrigin);
        Assert.Equal(ApplicationOrigin.Custom, custom.SourceOrigin);
        Assert.Equal("catalog:same01", WorkbenchApplicationCatalog.ApplicationIdFor(
            PackagerDiscoveryService.DefinitionRow(overridden, overridden.SourcePath)));
        Assert.Equal("custom:private01", WorkbenchApplicationCatalog.ApplicationIdFor(
            PackagerDiscoveryService.DefinitionRow(custom, custom.SourcePath)));
    }

    [Fact]
    public void ExternalIconLookupChecksUserOverridesBeforeDefinitionAndBundledIcons()
    {
        var definition = new AppDefinition
        {
            Id = "icon01",
            SourcePath = Path.Combine(IconPackService.BundledDefinitionsDirectory(), "reader.json")
        };

        var iconRoots = IconExtractionService.ExternalIconsDirectories(definition);

        Assert.Equal(IconPackService.IconsDirectory(IconPackService.UserDefinitionsDirectory()), iconRoots[0]);
        Assert.Equal(IconPackService.IconsDirectory(IconPackService.BundledDefinitionsDirectory()), iconRoots[1]);
    }
}
