using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The rules of the per-application columns of the Application Settings grid: the choices of a column, the choice that a stored entry
/// shows, and the edit that a choice makes. The edits go to the same <see cref="DeploymentConditionEntry"/> map that the package run reads
/// (DeploymentConditions of the settings file, keyed by the definition identifier), so a choice of the grid is the value that packaging uses.
/// </summary>
public static class ApplicationGridSettings
{
    public const string PackagerDefault = "Packager default";
    public const string ArchitectureX64 = "x64 only";
    public const string ArchitectureArm64 = "ARM64 only";
    public const string ArchitectureSplit = "x64 and ARM64";

    public const string InstallerDefault = "Installer default";
    public const string ScopeSystem = "System";
    public const string ScopeUser = "User";

    public const string FollowDefault = "Follow the default";
    public const string IncludeVersion = "Include version";
    public const string NoVersion = "No version";

    /// <summary>The choices of the package source column, for an application that offers an architecture choice.</summary>
    public static readonly IReadOnlyList<string> ArchitectureChoices = [PackagerDefault, ArchitectureX64, ArchitectureArm64, ArchitectureSplit];

    /// <summary>The choices of the install scope column, for an application that installs for the system and for the user.</summary>
    public static readonly IReadOnlyList<string> ScopeChoices = [InstallerDefault, ScopeSystem, ScopeUser];

    /// <summary>The choices of the version in title column.</summary>
    public static readonly IReadOnlyList<string> TitleChoices = [FollowDefault, IncludeVersion, NoVersion];

    public static string ArchitectureOf(DeploymentConditionEntry? entry)
    {
        if (entry is null) return PackagerDefault;
        if (string.Equals(entry.Split, "Architecture", StringComparison.OrdinalIgnoreCase)) return ArchitectureSplit;
        if (string.Equals(entry.Architecture, DeploymentConditionValues.X64, StringComparison.OrdinalIgnoreCase)) return ArchitectureX64;
        if (string.Equals(entry.Architecture, DeploymentConditionValues.Arm64, StringComparison.OrdinalIgnoreCase)) return ArchitectureArm64;
        return PackagerDefault;
    }

    public static string ScopeOf(DeploymentConditionEntry? entry) => entry?.InstallMode switch
    {
        DeploymentConditionValues.AllUsers => ScopeSystem,
        DeploymentConditionValues.CurrentUser => ScopeUser,
        _ => InstallerDefault
    };

    public static string TitleOf(DeploymentConditionEntry? entry) => entry?.TitleMode switch
    {
        ApplicationPackagerEngine.Services.ApplicationTitle.IncludeVersionToken => IncludeVersion,
        ApplicationPackagerEngine.Services.ApplicationTitle.NoVersionToken => NoVersion,
        _ => FollowDefault
    };

    /// <summary>The entry of an application, or null when the map holds none.</summary>
    public static DeploymentConditionEntry? Find(Dictionary<string, DeploymentConditionEntry> map, string? definitionId) =>
        string.IsNullOrWhiteSpace(definitionId) ? null
            : map.FirstOrDefault(p => string.Equals(p.Key, definitionId, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Applies a package source choice. A word that is not a choice changes nothing and returns false.</summary>
    public static bool SetArchitecture(Dictionary<string, DeploymentConditionEntry> map, string? definitionId, string? choice)
    {
        if (string.IsNullOrWhiteSpace(definitionId) || !ArchitectureChoices.Contains(choice ?? "")) return false;
        var entry = Entry(map, definitionId);
        if (string.Equals(entry.Split, "Architecture", StringComparison.OrdinalIgnoreCase)) entry.Split = DeploymentConditionValues.NoSplit;
        entry.Architecture = DeploymentConditionValues.Any;
        switch (choice)
        {
            case ArchitectureX64: entry.Architecture = DeploymentConditionValues.X64; break;
            case ArchitectureArm64: entry.Architecture = DeploymentConditionValues.Arm64; break;
            case ArchitectureSplit: entry.Split = "Architecture"; break;
        }
        Prune(map, definitionId);
        return true;
    }

    public static bool SetScope(Dictionary<string, DeploymentConditionEntry> map, string? definitionId, string? choice)
    {
        if (string.IsNullOrWhiteSpace(definitionId) || !ScopeChoices.Contains(choice ?? "")) return false;
        Entry(map, definitionId).InstallMode = choice switch
        {
            ScopeSystem => DeploymentConditionValues.AllUsers,
            ScopeUser => DeploymentConditionValues.CurrentUser,
            _ => ""
        };
        Prune(map, definitionId);
        return true;
    }

    /// <summary>The choice that a stored entry shows in the Installer type column: Packager default, EXE or MSI.</summary>
    public static string InstallerTypeOf(DeploymentConditionEntry? entry) =>
        ApplicationPackagerEngine.Services.InstallerTypeSwitch.Normalize(entry?.InstallerType) ?? PackagerDefault;

    /// <summary>The choices of the Installer type column, for an application that declares both EXE and MSI.</summary>
    public static readonly IReadOnlyList<string> InstallerTypeChoices =
        [PackagerDefault, ApplicationPackagerEngine.Services.InstallerTypeSwitch.Exe, ApplicationPackagerEngine.Services.InstallerTypeSwitch.Msi];

    /// <summary>Applies an Installer type choice (Packager default, EXE or MSI). A word that is not a choice changes nothing and returns false.</summary>
    public static bool SetInstallerType(Dictionary<string, DeploymentConditionEntry> map, string? definitionId, string? choice)
    {
        if (string.IsNullOrWhiteSpace(definitionId) || !InstallerTypeChoices.Contains(choice ?? "")) return false;
        Entry(map, definitionId).InstallerType = choice == PackagerDefault ? "" : choice!;
        Prune(map, definitionId);
        return true;
    }

    public static bool SetTitle(Dictionary<string, DeploymentConditionEntry> map, string? definitionId, string? choice)
    {
        if (string.IsNullOrWhiteSpace(definitionId) || !TitleChoices.Contains(choice ?? "")) return false;
        Entry(map, definitionId).TitleMode = choice switch
        {
            IncludeVersion => ApplicationPackagerEngine.Services.ApplicationTitle.IncludeVersionToken,
            NoVersion => ApplicationPackagerEngine.Services.ApplicationTitle.NoVersionToken,
            _ => ""
        };
        Prune(map, definitionId);
        return true;
    }

    /// <summary>Sets the network requirement: Any, VpnOnly or OnSiteOnly.</summary>
    public static void SetNetwork(Dictionary<string, DeploymentConditionEntry> map, string definitionId, string token)
    {
        Entry(map, definitionId).Network = token;
        Prune(map, definitionId);
    }

    /// <summary>Turns the Network split on or off; a split of another kind (Architecture, Language) is left as it is.</summary>
    public static void SetNetworkSplit(Dictionary<string, DeploymentConditionEntry> map, string definitionId, bool on)
    {
        var entry = Entry(map, definitionId);
        if (on) entry.Split = "Network";
        else if (string.Equals(entry.Split, "Network", StringComparison.OrdinalIgnoreCase)) entry.Split = DeploymentConditionValues.NoSplit;
        Prune(map, definitionId);
    }

    /// <summary>Sets the install or the uninstall command override of an application (null leaves that one as it is); an entry with both empty is removed.</summary>
    public static void SetCommand(Dictionary<string, CommandOverrideEntry> map, string definitionId, string? install, string? uninstall)
    {
        var key = map.Keys.FirstOrDefault(k => string.Equals(k, definitionId, StringComparison.OrdinalIgnoreCase)) ?? definitionId;
        if (!map.TryGetValue(key, out var entry)) map[key] = entry = new CommandOverrideEntry();
        if (install is not null) entry.Install = install;
        if (uninstall is not null) entry.Uninstall = uninstall;
        if (string.IsNullOrWhiteSpace(entry.Install) && string.IsNullOrWhiteSpace(entry.Uninstall)) map.Remove(key);
    }

    public static Dictionary<string, CommandOverrideEntry> CopyCommands(Dictionary<string, CommandOverrideEntry>? source)
    {
        var copy = new Dictionary<string, CommandOverrideEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, e) in source ?? []) copy[key] = new CommandOverrideEntry { Install = e.Install, Uninstall = e.Uninstall };
        return copy;
    }

    private static DeploymentConditionEntry Entry(Dictionary<string, DeploymentConditionEntry> map, string definitionId)
    {
        var existing = map.Keys.FirstOrDefault(k => string.Equals(k, definitionId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return map[existing];
        var entry = new DeploymentConditionEntry();
        map[definitionId] = entry;
        return entry;
    }

    /// <summary>An entry that holds no choice is removed, so the file holds only what differs from the shipped behaviour.</summary>
    private static void Prune(Dictionary<string, DeploymentConditionEntry> map, string definitionId)
    {
        var key = map.Keys.FirstOrDefault(k => string.Equals(k, definitionId, StringComparison.OrdinalIgnoreCase));
        if (key is null) return;
        var e = map[key];
        var empty = string.Equals(e.Architecture, DeploymentConditionValues.Any, StringComparison.OrdinalIgnoreCase)
            && (e.Languages is null || e.Languages.Length == 0)
            && string.Equals(e.Network, DeploymentConditionValues.Any, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Split, DeploymentConditionValues.NoSplit, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(e.InstallMode) && string.IsNullOrEmpty(e.TitleMode) && string.IsNullOrEmpty(e.InstallerType);
        if (empty) map.Remove(key);
    }

    /// <summary>A copy of the map that the grid edits; the stored map is not changed until the caller copies the result back.</summary>
    public static Dictionary<string, DeploymentConditionEntry> Copy(Dictionary<string, DeploymentConditionEntry>? source)
    {
        var copy = new Dictionary<string, DeploymentConditionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, e) in source ?? [])
            copy[key] = new DeploymentConditionEntry
            {
                Architecture = e.Architecture, Languages = [.. e.Languages ?? []], Network = e.Network,
                Split = e.Split, InstallMode = e.InstallMode, TitleMode = e.TitleMode, InstallerType = e.InstallerType
            };
        return copy;
    }

    /// <summary>
    /// After Save Settings: brings the Migrated workbench profiles up to date with the saved conditions, so a run in this session reads the
    /// choices of the grid also for an application that has a Migrated profile (a profile outranks the saved map). The pass is the one that
    /// runs at start; it is idempotent. A failure leaves the profiles as they are until the next start, and the method says so with false.
    /// </summary>
    public static bool RefreshMigratedProfiles(AppSettings settings, string? packagersRoot)
    {
        try
        {
            LegacyPreferenceMigration.Run(WorkbenchStore.Open(), JsonSerializer.SerializeToNode(settings),
                new WorkbenchDiscoveryRoots
                {
                    CatalogDefinitionsRoot = IconPackService.BundledDefinitionsDirectory(),
                    CatalogOverridesRoot = AppDataPaths.UserDefinitionsRoot,
                    CustomDefinitionsRoot = AppDataPaths.UserDefinitionsRoot,
                    ImportedDefinitionsRoot = ApplicationPackagerEngine.Services.VendorImport.ImportedDefinitionStore.DefaultRoot,
                    PackagersRoot = packagersRoot
                });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}
