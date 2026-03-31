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

    // Header tag patterns — match "Vendor: value" or "# Vendor: value"
    [GeneratedRegex(@"^\s*(?:#\s*)?Vendor\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VendorRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?(?:App|Application)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AppRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?CMName\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CMNameRegex();

    [GeneratedRegex(@"^\s*(?:#\s*)?VendorUrl\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VendorUrlRegex();

    [GeneratedRegex(@"^\s*\.SYNOPSIS\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SynopsisRegex();

    /// <summary>
    /// Resolves the Packagers folder using the priority chain:
    /// 1. Settings override (if non-empty and exists)
    /// 2. Co-located Packagers/ next to the exe
    /// 3. Dev default: C:\projects\applicationpackager\Packagers
    /// </summary>
    public string? ResolvePackagersRoot()
    {
        var settings = settingsService.Load();

        // 1. Settings override
        if (!string.IsNullOrWhiteSpace(settings.PackagersRoot) && Directory.Exists(settings.PackagersRoot))
            return settings.PackagersRoot;

        // 2. Co-located
        var coLocated = Path.Combine(AppContext.BaseDirectory, "Packagers");
        if (Directory.Exists(coLocated))
            return coLocated;

        // 3. Dev default
        var devDefault = @"C:\projects\applicationpackager\Packagers";
        if (Directory.Exists(devDefault))
            return devDefault;

        return null;
    }

    /// <summary>
    /// Discovers all packager scripts and parses their header metadata.
    /// Filters out hidden applications per settings.
    /// </summary>
    public async Task<List<PackagerInfo>> DiscoverAsync(CancellationToken ct = default)
    {
        var root = ResolvePackagersRoot();
        if (root is null)
            return [];

        var settings = settingsService.Load();
        var hidden = new HashSet<string>(settings.HiddenApplications, StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            // Discover JSON definitions first — they take precedence over PS1 scripts
            var jsonResults = DiscoverJsonDefinitions(hidden);
            var jsonAppNames = new HashSet<string>(
                jsonResults.Select(j => j.Application), StringComparer.OrdinalIgnoreCase);

            var files = Directory.GetFiles(root)
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

                    // Skip hidden applications
                    if (hidden.Contains(meta.Application ?? ""))
                        continue;

                    // Skip PS1 if a JSON definition exists for the same app
                    if (jsonAppNames.Contains(meta.Application ?? ""))
                        continue;

                    var status = "Ready";
                    if (string.IsNullOrEmpty(meta.Vendor) || string.IsNullOrEmpty(meta.Application))
                        status = "Missing metadata (Vendor/App)";

                    results.Add(new PackagerInfo
                    {
                        Vendor = meta.Vendor ?? "",
                        Application = meta.Application ?? "",
                        CMName = meta.CMName ?? meta.Application ?? "",
                        Script = scriptName,
                        FullPath = filePath,
                        VendorUrl = meta.VendorUrl ?? "",
                        Description = meta.Description ?? "",
                        Status = status
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

            // Add JSON-defined apps (already discovered above)
            results.AddRange(jsonResults);

            // Sort combined list
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

        // Check Definitions folder alongside the exe
        var defsDir = Path.Combine(AppContext.BaseDirectory, "Definitions");
        if (!Directory.Exists(defsDir)) return results;

        foreach (var jsonPath in Directory.GetFiles(defsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                var def = JsonSerializer.Deserialize<AppDefinition>(json, JsonOptions);
                if (def is null) continue;

                if (hidden is not null && hidden.Contains(def.Application)) continue;

                results.Add(new PackagerInfo
                {
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
                });
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Discovers ALL packager scripts without filtering hidden apps.
    /// Used by the Application Selector in Settings.
    /// </summary>
    public async Task<List<PackagerInfo>> DiscoverAllAsync(CancellationToken ct = default)
    {
        var root = ResolvePackagersRoot();
        if (root is null)
            return [];

        return await Task.Run(() =>
        {
            var files = Directory.GetFiles(root)
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
                    results.Add(new PackagerInfo
                    {
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

    private sealed record ScriptMetadata(
        string? Vendor, string? Application, string? CMName,
        string? VendorUrl, string? Description);

    private static ScriptMetadata ParseMetadata(string path)
    {
        string? vendor = null, app = null, cmName = null, vendorUrl = null, description = null;
        var inSynopsis = false;
        var lineCount = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            if (++lineCount > 200) break;

            var line = rawLine.TrimStart('\uFEFF');

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

        return new ScriptMetadata(vendor, app, cmName ?? app, vendorUrl, description);
    }
}
