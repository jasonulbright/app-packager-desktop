using System.Text.Json;
using System.Text.RegularExpressions;

// PS1 → JSON Definition Migration Tool
// Parses structured PS1 packager scripts and generates JSON app definitions.
// Usage: MigratePs1ToJson.exe <ps1Dir> <jsonOutputDir>

var ps1Dir = args.Length > 0 ? args[0] : @"C:\projects\applicationpackager\Packagers";
var outputDir = args.Length > 1 ? args[1] : @"C:\temp\migrated-definitions";

Console.WriteLine($"PS1 → JSON Migration Tool");
Console.WriteLine($"Source: {ps1Dir}");
Console.WriteLine($"Output: {outputDir}");
Console.WriteLine();

Directory.CreateDirectory(outputDir);

var ps1Files = Directory.GetFiles(ps1Dir, "package-*.ps1");
Console.WriteLine($"Found {ps1Files.Length} PS1 packagers");
Console.WriteLine();

var migrated = 0;
var skipped = 0;

foreach (var ps1File in ps1Files.OrderBy(f => f))
{
    var fileName = Path.GetFileName(ps1File);
    var content = File.ReadAllText(ps1File);

    // Parse header metadata block
    var vendor = ExtractHeader(content, "Vendor");
    var app = ExtractHeader(content, "App");
    var cmName = ExtractHeader(content, "CMName");
    var vendorUrl = ExtractHeader(content, "VendorUrl");

    if (string.IsNullOrEmpty(vendor) || string.IsNullOrEmpty(app))
    {
        Console.WriteLine($"SKIP: {fileName} — no Vendor/App header");
        skipped++;
        continue;
    }

    // Parse scraper pattern
    var (scraperUrl, scraperPattern, scraperType) = ExtractScraper(content);

    // Parse download URL
    var downloadUrl = ExtractDownloadUrl(content);

    // Parse install/uninstall args
    var (installArgs, uninstallArgs) = ExtractInstallArgs(content);

    // Parse installer type
    var installerType = content.Contains(".msi", StringComparison.OrdinalIgnoreCase) ? "MSI" : "EXE";

    // Determine architecture
    var arch = app.Contains("x86") ? "x86" : "x64";

    // Generate definition
    var def = new Dictionary<string, object?>
    {
        ["Schema"] = 1,
        ["Id"] = Guid.NewGuid().ToString("N")[..16],
        ["Vendor"] = vendor,
        ["Application"] = app,
        ["CMName"] = cmName ?? app,
        ["Architecture"] = arch,
        ["VendorUrl"] = vendorUrl ?? "",
        ["InstallerType"] = installerType,
        ["Acquisition"] = new Dictionary<string, object>(),
        ["VersionScraper"] = new Dictionary<string, object?>
        {
            ["Type"] = scraperType,
            ["Url"] = scraperUrl ?? "",
            ["Pattern"] = scraperPattern ?? ""
        },
        ["DownloadUrl"] = downloadUrl ?? "",
        ["InstallArgs"] = installArgs ?? "",
        ["UninstallArgs"] = uninstallArgs ?? "",
        ["Detection"] = new Dictionary<string, object?>
        {
            ["Type"] = "RegistryKeyValue",
            ["RegistryHive"] = "HKLM",
            ["RegistryKeyTemplate"] = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{ProductCode}",
            ["ValueName"] = "DisplayVersion",
            ["PropertyType"] = "Version",
            ["Operator"] = "GreaterEquals",
            ["Is64Bit"] = arch == "x64"
        },
        ["RunningProcess"] = Array.Empty<string>()
    };

    var jsonName = fileName.Replace("package-", "").Replace(".ps1", ".json");
    var jsonPath = Path.Combine(outputDir, jsonName);
    var json = JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(jsonPath, json);

    Console.WriteLine($"OK: {fileName} → {jsonName}");
    Console.WriteLine($"    {vendor} | {app} | {scraperType} | {installerType}");
    migrated++;
}

Console.WriteLine();
Console.WriteLine($"Migrated: {migrated} | Skipped: {skipped}");

// --- Extraction helpers ---

static string? ExtractHeader(string content, string key)
{
    var match = Regex.Match(content, $@"^{key}:\s*(.+)$", RegexOptions.Multiline);
    return match.Success ? match.Groups[1].Value.Trim() : null;
}

static (string? Url, string? Pattern, string Type) ExtractScraper(string content)
{
    // Look for curl/IWR URL + regex pattern
    var urlMatch = Regex.Match(content, @"curl\.exe\s+-[^'""]*\s+['""]?(https?://[^'"")\s]+)", RegexOptions.IgnoreCase);
    if (!urlMatch.Success)
        urlMatch = Regex.Match(content, @"Invoke-WebRequest\s+['""]?(https?://[^'"")\s]+)", RegexOptions.IgnoreCase);
    if (!urlMatch.Success)
        urlMatch = Regex.Match(content, @"\$(?:url|apiUrl|pageUrl|downloadUrl)\s*=\s*['""]?(https?://[^'"")\s]+)", RegexOptions.IgnoreCase);

    var patternMatch = Regex.Match(content, @"\[regex\]['""](.+?)['""]|\-match\s+['""](.+?)['""]|::Match\([^,]+,\s*['""](.+?)['""]", RegexOptions.IgnoreCase);

    var url = urlMatch.Success ? urlMatch.Groups[1].Value : null;
    var pattern = patternMatch.Success
        ? (patternMatch.Groups[1].Value != "" ? patternMatch.Groups[1].Value
           : patternMatch.Groups[2].Value != "" ? patternMatch.Groups[2].Value
           : patternMatch.Groups[3].Value)
        : null;

    // Determine scraper type
    var type = "HttpRegex";
    if (content.Contains("ConvertFrom-Json", StringComparison.OrdinalIgnoreCase) ||
        content.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
        type = "JsonQuery";
    if (content.Contains("FileVersionInfo", StringComparison.OrdinalIgnoreCase) ||
        content.Contains("Get-Item.*VersionInfo", StringComparison.OrdinalIgnoreCase))
        type = "FileVersion";

    return (url, pattern, type);
}

static string? ExtractDownloadUrl(string content)
{
    // Look for download URL template
    var match = Regex.Match(content, @"\$(?:downloadUrl|url)\s*=\s*""(https?://[^""]+\$[^""]+)""", RegexOptions.IgnoreCase);
    if (match.Success)
    {
        var url = match.Groups[1].Value;
        // Convert PS variable interpolation to {version} placeholder
        url = Regex.Replace(url, @"\$(?:Version|ver|latestVersion)", "{version}", RegexOptions.IgnoreCase);
        return url;
    }
    return null;
}

static (string? Install, string? Uninstall) ExtractInstallArgs(string content)
{
    // Look for install args in Start-Process calls
    var installMatch = Regex.Match(content, @"-ArgumentList\s+['""]([^'""]+)['""].*(?:install|stage)", RegexOptions.IgnoreCase);
    if (!installMatch.Success)
        installMatch = Regex.Match(content, @"InstallArgs\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);

    var uninstallMatch = Regex.Match(content, @"UninstallArgs\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
    if (!uninstallMatch.Success)
        uninstallMatch = Regex.Match(content, @"(?:uninstall|remove).*-ArgumentList\s+['""]([^'""]+)['""]", RegexOptions.IgnoreCase);

    return (
        installMatch.Success ? installMatch.Groups[1].Value : null,
        uninstallMatch.Success ? uninstallMatch.Groups[1].Value : null
    );
}
