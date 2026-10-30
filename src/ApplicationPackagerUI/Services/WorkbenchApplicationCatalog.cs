using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>One application in the Workbench picker.</summary>
public sealed class WorkbenchApplicationItem
{
    public required string ApplicationId { get; init; }
    public required ApplicationOrigin Origin { get; init; }
    public string DisplayName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    public string DisplayLabel => string.IsNullOrWhiteSpace(Publisher) ? DisplayName : Publisher + " - " + DisplayName;
    public string? SourcePath { get; init; }
    public bool IsJsonDefinition { get; init; }

    public bool HasRemoteSource { get; init; }
    public bool Runnable { get; init; }
    public IReadOnlyList<string> SupportsVariants { get; init; } = [];
    public IReadOnlyList<string> SupportsInstallModes { get; init; } = [];
    public string? IconPackDirectory { get; init; }
    public string? IconPackBaseName { get; init; }
    public string? IconSource { get; init; }
    public string? StageSearchRoot { get; init; }

    public string SourceType => Origin switch
    {
        ApplicationOrigin.Catalog => "Catalog packager",
        ApplicationOrigin.Custom => "User-authored packager",
        _ => "Bring your own installer"
    };

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Builds the Workbench application list: shipped JSON definitions and PS1 packagers
/// (catalog), user-authored definitions (custom) and persisted BYO applications, with
/// the metadata each page needs.
/// </summary>
public sealed class WorkbenchApplicationCatalog(SettingsService settingsService, PackagerDiscoveryService discoveryService)
{
    public WorkbenchDiscoveryRoots Roots() => new()
    {
        CatalogDefinitionsRoot = IconPackService.BundledDefinitionsDirectory(),
        CatalogOverridesRoot = AppDataPaths.UserDefinitionsRoot,
        CustomDefinitionsRoot = AppDataPaths.UserDefinitionsRoot,
        PackagersRoot = discoveryService.ResolvePackagersRoot()
    };

    /// <summary>The workbench application id of a Packages grid row.</summary>
    public static string ApplicationIdFor(IPackagerExecutionRequest packager) => packager.IsJsonDefined
        ? ApplicationIds.New(packager.DefinitionOrigin, name: packager.Uid)
        : ApplicationIds.New(ApplicationOrigin.Catalog, path: packager.FullPath);

    public static string ApplicationIdForDefinition(string definitionId) => ApplicationIds.New(ApplicationOrigin.Custom, name: definitionId);

    public async Task<IReadOnlyList<WorkbenchApplicationItem>> LoadAsync(WorkbenchStore store, CancellationToken ct = default)
    {
        var settings = settingsService.Load();
        var definitions = await discoveryService.LoadDefinitionsAsync(ct);
        var scripts = (await discoveryService.DiscoverAllAsync(ct)).Where(p => !p.IsJsonDefined).ToList();
        var roots = Roots();
        var discovered = await Task.Run(() => store.DiscoverApplications(roots), ct);

        var definitionById = new Dictionary<string, AppDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            try { definitionById.TryAdd(ApplicationIds.New(definition.SourceOrigin, name: definition.Id), definition); }
            catch (ArgumentException) { }
        }
        var scriptById = new Dictionary<string, PackagerInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts) scriptById.TryAdd(ApplicationIdFor(script), script);

        var items = new List<WorkbenchApplicationItem>();
        foreach (var application in discovered)
        {
            WorkbenchApplicationItem item;
            if (definitionById.TryGetValue(application.ApplicationId, out var definition))
            {
                var iconPath = IconExtractionService.FindExternalIconPath(definition);
                item = new WorkbenchApplicationItem
                {
                    ApplicationId = application.ApplicationId,
                    Origin = application.Origin,
                    DisplayName = definition.Application,
                    Publisher = definition.Vendor,
                    SourcePath = definition.SourcePath,
                    IsJsonDefinition = true,
                    HasRemoteSource = !ApplicationPackagerEngine.Services.AddFromInstaller.AddFromInstallerService.IsLocalFileDefinition(definition),
                    Runnable = true,
                    SupportsVariants = definition.SupportsVariants ?? [],
                    SupportsInstallModes = definition.SupportsInstallModes ?? [],
                    IconPackDirectory = iconPath is null
                        ? IconExtractionService.ExternalIconsDirectory(definition)
                        : Path.GetDirectoryName(iconPath),
                    IconPackBaseName = IconExtractionService.ExternalIconBaseName(definition),
                    IconSource = definition.IconSource,
                    StageSearchRoot = string.IsNullOrWhiteSpace(settings.DownloadRoot)
                        ? null
                        : Path.Combine(settings.DownloadRoot, $"{definition.Application.Replace(" ", "")}-{definition.Architecture}")
                };
            }
            else if (scriptById.TryGetValue(application.ApplicationId, out var script))
            {
                item = new WorkbenchApplicationItem
                {
                    ApplicationId = application.ApplicationId,
                    Origin = application.Origin,
                    DisplayName = script.Application,
                    Publisher = script.Vendor,
                    Description = script.Description,
                    SourcePath = script.FullPath,
                    Runnable = application.Runnable,
                    IconPackDirectory = roots.PackagersRoot is null ? null : Path.Combine(roots.PackagersRoot, "Icons"),
                    IconPackBaseName = PackagerBaseName(script.FullPath),
                    IconSource = "Installer"
                };
            }
            else
            {
                item = new WorkbenchApplicationItem
                {
                    ApplicationId = application.ApplicationId,
                    Origin = application.Origin,
                    DisplayName = application.DisplayName,
                    Publisher = application.Publisher ?? "",
                    SourcePath = application.SourcePath,
                    Runnable = application.Runnable,
                    IconPackDirectory = roots.PackagersRoot is null ? null : Path.Combine(roots.PackagersRoot, "Icons"),
                    IconPackBaseName = application.SourcePath is null ? null : PackagerBaseName(application.SourcePath)
                };
            }

            items.Add(item);
        }

        items.Sort((a, b) => string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    private static string PackagerBaseName(string scriptPath)
    {
        var name = Path.GetFileNameWithoutExtension(scriptPath);
        return name.StartsWith("package-", StringComparison.OrdinalIgnoreCase) ? name["package-".Length..] : name;
    }
}
