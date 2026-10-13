using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public partial class PackagerDiscoveryService(SettingsService settingsService)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex FilePattern = new(@"^package-.*\.ps1$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"^\s*(?:#\s*)?Vendor\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VendorRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?UID\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UidRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?(?:App|Application)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AppRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?CMName\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CMNameRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?VendorUrl\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VendorUrlRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?CPE\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CpeRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?SecurityAdvisoryUrl\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityAdvisoryUrlRegex();

    [GeneratedRegex(@"^\s*\.SYNOPSIS\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SynopsisRegex();

    /// <summary>
    /// The first existing folder of <see cref="PackagersRootCandidates"/>, or null when
    /// none exists.
    /// </summary>
    public string? ResolvePackagersRoot() =>
        PackagersRootCandidates(settingsService.Load().PackagersRoot, AppContext.BaseDirectory)
            .FirstOrDefault(Directory.Exists);

    /// <summary>
    /// The Packagers folder candidates in priority order: the saved setting when it is not
    /// empty, then the Packagers folder beside the executable.
    /// </summary>
    public static IReadOnlyList<string> PackagersRootCandidates(string? configuredRoot, string baseDirectory)
    {
        var candidates = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(configuredRoot)) candidates.Add(configuredRoot);
        candidates.Add(Path.Combine(baseDirectory, "Packagers"));
        return candidates;
    }

    /// <summary>
    /// Discovers all packager scripts and parses their header metadata.
    /// Filters out hidden applications per settings.
    /// </summary>
    public async Task<List<PackagerInfo>> DiscoverAsync(CancellationToken ct = default)
    {
        // The shipped definitions sit beside the executable and need no PS1 folder;
        // scripts are added on top when a folder resolves.
        var root = ResolvePackagersRoot();

        var settings = settingsService.Load();
        var hidden = new HashSet<string>(settings.HiddenApplications, StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            // Discover JSON definitions first — they take precedence over PS1 scripts
            var jsonResults = DiscoverJsonDefinitions(hidden);
            var jsonAppNames = new HashSet<string>(
                jsonResults.Select(j => j.Application), StringComparer.OrdinalIgnoreCase);

            var files = root is null
                ? []
                : Directory.GetFiles(root)
                    .Where(f => FilePattern.IsMatch(Path.GetFileName(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var results = new List<PackagerInfo>(files.Count);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var meta = ParseMetadata(filePath);
                    var scriptName = Path.GetFileName(filePath);

                    if (string.IsNullOrEmpty(meta.Uid))
                        continue;

                    if (hidden.Contains(meta.Uid))
                        continue;

                    if (jsonAppNames.Contains(meta.Application ?? ""))
                        continue;

                    results.Add(new PackagerInfo
                    {
                        Uid = meta.Uid,
                        Vendor = meta.Vendor ?? "",
                        Application = meta.Application ?? "",
                        CMName = meta.CMName ?? meta.Application ?? "",
                        Script = scriptName,
                        FullPath = filePath,
                        VendorUrl = meta.VendorUrl ?? "",
                        Cpe = meta.Cpe ?? "",
                        AdvisoryUrl = meta.AdvisoryUrl ?? "",
                        Description = meta.Description ?? "",
                        Status = "Ready"
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new PackagerInfo
                    {
                        Script = Path.GetFileName(filePath),
                        FullPath = filePath,
                        Status = $"Read error: {ex.Message}"
                    });
                }
            }

            results.AddRange(jsonResults);

            results.Sort((a, b) => string.Compare(a.Application, b.Application, StringComparison.OrdinalIgnoreCase));

            return results;
        }, ct);
    }

    /// <summary>Discovers effective definitions: user files override shipped files with the same Id.</summary>
    private static List<PackagerInfo> DiscoverJsonDefinitions(HashSet<string>? hidden = null)
    {
        var results = new List<PackagerInfo>();
        foreach (var definition in LoadEffectiveDefinitions(
                     IconPackService.BundledDefinitionsDirectory(), AppDataPaths.UserDefinitionsRoot))
        {
            if (hidden is not null && hidden.Contains(definition.Id)) continue;
            results.Add(DefinitionRow(definition, definition.SourcePath, definition.SourceOrigin));
        }

        return results;
    }

    /// <summary>
    /// Loads shipped definitions and user definitions. A user file with the same Id
    /// overrides the shipped file while retaining its catalog identity; new user Ids
    /// use the custom identity namespace.
    /// </summary>
    public static List<AppDefinition> LoadEffectiveDefinitions(
        string catalogRoot,
        string userRoot,
        CancellationToken ct = default)
    {
        var catalogDefinitions = ReadDefinitions(catalogRoot, ApplicationOrigin.Catalog, ct);
        var catalogIds = catalogDefinitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effective = new Dictionary<string, AppDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in catalogDefinitions) effective.TryAdd(definition.Id, definition);

        var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in ReadDefinitions(userRoot, ApplicationOrigin.Custom, ct))
        {
            if (!userIds.Add(definition.Id)) continue;
            if (catalogIds.Contains(definition.Id)) definition.SourceOrigin = ApplicationOrigin.Catalog;
            effective[definition.Id] = definition;
        }

        return effective.Values.ToList();
    }

    private static List<AppDefinition> ReadDefinitions(
        string root,
        ApplicationOrigin origin,
        CancellationToken ct)
    {
        var results = new List<AppDefinition>();
        if (!Directory.Exists(root)) return results;
        foreach (var jsonPath in Directory.GetFiles(root, "*.json")
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var definition = JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(jsonPath), JsonOptions);
                if (definition is null || string.IsNullOrWhiteSpace(definition.Id)) continue;
                definition.SourcePath = Path.GetFullPath(jsonPath);
                definition.SourceOrigin = origin;
                results.Add(definition);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A bad local definition is isolated; other definitions remain discoverable.
            }
        }
        return results;
    }

    /// <summary>The run row of one JSON definition file, as the Packages grid lists it.</summary>
    public static PackagerInfo DefinitionRow(
        AppDefinition def,
        string jsonPath,
        ApplicationOrigin? origin = null) => new()
    {
        Uid = def.Id,
        Vendor = def.Vendor,
        Application = def.Application,
        CMName = def.CMName,
        Script = Path.GetFileName(jsonPath),
        FullPath = jsonPath,
        VendorUrl = def.VendorUrl,
        Cpe = def.Cpe?.Trim() ?? "",
        AdvisoryUrl = def.SecurityAdvisoryUrl?.Trim() ?? "",
        Description = $"{def.InstallerType} ({def.Architecture})",
        IsJsonDefined = true,
        DefinitionPath = jsonPath,
        DefinitionOrigin = origin ?? def.SourceOrigin,
        Status = "Ready"
    };

    /// <summary>
    /// Discovers ALL packager scripts without filtering hidden apps.
    /// Used by the Application Selector in Settings.
    /// </summary>
    public async Task<List<PackagerInfo>> DiscoverAllAsync(CancellationToken ct = default)
    {
        var root = ResolvePackagersRoot();

        return await Task.Run(() =>
        {
            var files = root is null
                ? []
                : Directory.GetFiles(root)
                    .Where(f => FilePattern.IsMatch(Path.GetFileName(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var results = new List<PackagerInfo>(files.Count);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var meta = ParseMetadata(filePath);
                    if (string.IsNullOrEmpty(meta.Uid)) continue;

                    results.Add(new PackagerInfo
                    {
                        Uid = meta.Uid,
                        Vendor = meta.Vendor ?? "",
                        Application = meta.Application ?? "",
                        CMName = meta.CMName ?? meta.Application ?? "",
                        Script = Path.GetFileName(filePath),
                        FullPath = filePath,
                        VendorUrl = meta.VendorUrl ?? "",
                        Description = meta.Description ?? "",
                    });
                }
                catch { }
            }

            results.AddRange(DiscoverJsonDefinitions());
            results.Sort((a, b) => string.Compare(a.Application, b.Application, StringComparison.OrdinalIgnoreCase));

            return results;
        }, ct);
    }

    /// <summary>
    /// The JSON app definitions themselves, for callers that need fields
    /// the PackagerInfo projection drops. Sorted by vendor then application.
    /// </summary>
    public async Task<List<AppDefinition>> LoadDefinitionsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = LoadEffectiveDefinitions(
                IconPackService.BundledDefinitionsDirectory(), AppDataPaths.UserDefinitionsRoot, ct);

            results.Sort((a, b) =>
            {
                var vendor = string.Compare(a.Vendor, b.Vendor, StringComparison.OrdinalIgnoreCase);
                return vendor != 0
                    ? vendor
                    : string.Compare(a.Application, b.Application, StringComparison.OrdinalIgnoreCase);
            });
            return results;
        }, ct);
    }

    private sealed record ScriptMetadata(
        string? Uid, string? Vendor, string? Application, string? CMName,
        string? VendorUrl, string? Description, string? Cpe = null, string? AdvisoryUrl = null);

    private static ScriptMetadata ParseMetadata(string path)
    {
        string? uid = null, vendor = null, app = null, cmName = null, vendorUrl = null, description = null, cpe = null, advisoryUrl = null;
        var inSynopsis = false;
        var lineCount = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            if (++lineCount > 200) break;

            var line = rawLine.TrimStart('\uFEFF');

            if (uid is null)
            {
                var m = UidRegex().Match(line);
                if (m.Success) { uid = m.Groups[1].Value.Trim(); continue; }
            }

            if (vendor is null)
            {
                var m = VendorRegex().Match(line);
                if (m.Success) { vendor = m.Groups[1].Value.Trim(); continue; }
            }

            if (app is null)
            {
                var m = AppRegex().Match(line);
                if (m.Success) { app = m.Groups[1].Value.Trim(); continue; }
            }

            if (cmName is null)
            {
                var m = CMNameRegex().Match(line);
                if (m.Success) { cmName = m.Groups[1].Value.Trim(); continue; }
            }

            if (vendorUrl is null)
            {
                var m = VendorUrlRegex().Match(line);
                if (m.Success) { vendorUrl = m.Groups[1].Value.Trim(); continue; }
            }

            if (cpe is null)
            {
                var m = CpeRegex().Match(line);
                if (m.Success) { cpe = m.Groups[1].Value.Trim(); continue; }
            }

            if (advisoryUrl is null)
            {
                var m = SecurityAdvisoryUrlRegex().Match(line);
                if (m.Success) { advisoryUrl = m.Groups[1].Value.Trim(); continue; }
            }

            if (description is null && SynopsisRegex().IsMatch(line))
            {
                inSynopsis = true;
                continue;
            }

            if (inSynopsis && description is null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    description = trimmed;
                    inSynopsis = false;
                }
            }
        }

        return new ScriptMetadata(uid, vendor, app, cmName ?? app, vendorUrl, description, cpe, advisoryUrl);
    }
}
