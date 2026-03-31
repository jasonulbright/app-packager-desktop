using System.IO;
using System.Text.RegularExpressions;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public partial class PackagerDiscoveryService(SettingsService settingsService)
{
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

            return results;
        }, ct);
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
