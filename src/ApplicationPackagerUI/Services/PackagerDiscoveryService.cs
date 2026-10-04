using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
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

    /// <summary>
    /// Discovers JSON app definitions from the Definitions folder (engine DLL output).
    /// </summary>
    private static List<PackagerInfo> DiscoverJsonDefinitions(HashSet<string>? hidden = null)
    {
        var results = new List<PackagerInfo>();

        var defsDir = Path.Combine(AppContext.BaseDirectory, "Definitions");
        if (!Directory.Exists(defsDir)) return results;

        foreach (var jsonPath in Directory.GetFiles(defsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                var def = JsonSerializer.Deserialize<AppDefinition>(json, JsonOptions);
                if (def is null || string.IsNullOrEmpty(def.Id)) continue;

                if (hidden is not null && hidden.Contains(def.Id)) continue;

                results.Add(DefinitionRow(def, jsonPath));
            }
            catch { }
        }

        return results;
    }

    /// <summary>The run row of one JSON definition file, as the Packages grid lists it.</summary>
    public static PackagerInfo DefinitionRow(AppDefinition def, string jsonPath) => new()
    {
        Uid = def.Id,
        Vendor = def.Vendor,
        Application = def.Application,
        CMName = def.CMName,
        Script = Path.GetFileName(jsonPath),
        FullPath = jsonPath,
        VendorUrl = def.VendorUrl,
        Description = $"{def.InstallerType} ({def.Architecture})",
        IsJsonDefined = true,
        DefinitionPath = jsonPath,
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
            var results = new List<AppDefinition>();
            var defsDir = Path.Combine(AppContext.BaseDirectory, "Definitions");
            if (!Directory.Exists(defsDir)) return results;

            foreach (var jsonPath in Directory.GetFiles(defsDir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var def = JsonSerializer.Deserialize<AppDefinition>(
                        File.ReadAllText(jsonPath), JsonOptions);
                    if (def is null || string.IsNullOrEmpty(def.Id)) continue;
                    def.SourcePath = jsonPath;
                    results.Add(def);
                }
                catch { }
            }

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
        string? VendorUrl, string? Description);

    private static ScriptMetadata ParseMetadata(string path)
    {
        string? uid = null, vendor = null, app = null, cmName = null, vendorUrl = null, description = null;
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

        return new ScriptMetadata(uid, vendor, app, cmName ?? app, vendorUrl, description);
    }
}
