using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>One icon in a pack manifest.</summary>
public sealed class IconPackEntry
{
    public string File { get; set; } = "";
    public string Packager { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public int PixelSize { get; set; }
}

/// <summary>A pack manifest: pack version, the app version it targets, and its icons.</summary>
public sealed class IconPackManifest
{
    public string PackVersion { get; set; } = "";
    public string MinAppVersion { get; set; } = "";
    public List<IconPackEntry> Icons { get; set; } = new();
}

/// <summary>One pack entry resolved to a definition base name; several entries may share one pack file.</summary>
public sealed record IconPackMapping(IconPackEntry Entry, string DefinitionBaseName, bool Exact);

/// <summary>Outcome of a mapping pass over a manifest.</summary>
public sealed record IconPackMapResult(IReadOnlyList<IconPackMapping> Mappings, IReadOnlyList<IconPackEntry> Unmatched)
{
    /// <summary>Pack entries that reached at least one definition.</summary>
    public int MatchedEntries => Mappings.Select(m => m.Entry).Distinct().Count();

    /// <summary>Copies beyond the first for entries that reached more than one definition.</summary>
    public int FannedCopies => Mappings.Count - MatchedEntries;
}

/// <summary>Outcome of an install attempt. Never carries an exception: failures are messages.</summary>
public sealed record IconPackInstallResult(
    bool Installed,
    string Message,
    string? PackVersion = null,
    int Matched = 0,
    int Copied = 0,
    int Skipped = 0);

/// <summary>Installed-pack status for display.</summary>
public sealed record IconPackStatus(bool Installed, string PackVersion, int IconCount);

/// <summary>
/// Downloads the packager icon pack release, verifies it, and installs the PNGs
/// into the external icons folder under a definition-name mapping.
///
/// Pack files are named by PowerShell packager name; several packr definitions
/// carry a different file name for the same application, and an arch-collapsed
/// pack entry feeds every arch variant of the definition. Every failure path
/// returns a message rather than throwing: the caller is a button.
/// </summary>
public sealed class IconPackService
{
    public const string Repository = "jasonulbright/app-packager-icons";
    public const string PackAssetName = "icon-pack.zip";
    public const string ChecksumAssetName = "checksums.txt";
    private const string UserAgent = "packr-icon-pack";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly DownloadService _downloads = new();

    /// <summary>The external icons folder for a definitions folder.</summary>
    public static string IconsDirectory(string definitionsDirectory)
        => Path.Combine(definitionsDirectory, "Icons");

    /// <summary>The definitions folder shipped beside the executable.</summary>
    public static string DefaultDefinitionsDirectory()
        => Path.Combine(AppContext.BaseDirectory, "Definitions");

    // ---- Pure helpers ----

    /// <summary>
    /// Reads the sha256 for one file out of sha256sum output
    /// ("&lt;hash&gt; *&lt;name&gt;" or "&lt;hash&gt;  &lt;name&gt;"). Returns null when absent.
    /// </summary>
    public static string? ParseChecksum(string? checksumText, string fileName)
    {
        if (string.IsNullOrWhiteSpace(checksumText)) return null;

        foreach (var line in checksumText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var match = Regex.Match(trimmed, @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (!match.Success) continue;
            if (string.Equals(match.Groups[2].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
                return match.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>True when the file hashes to the expected sha256. A blank expectation is a mismatch.</summary>
    public static bool VerifyChecksum(string filePath, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        if (!File.Exists(filePath)) return false;

        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return actual == expectedSha256.Trim().ToLowerInvariant();
    }

    /// <summary>Reads a pack manifest. A missing, unreadable, or malformed file returns null.</summary>
    public static IconPackManifest? ReadManifest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<IconPackManifest>(File.ReadAllText(path), ManifestOptions);
            if (manifest is null) return null;
            manifest.Icons ??= new List<IconPackEntry>();
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the running app satisfies a pack's MinAppVersion. An unparseable
    /// version on either side reads as satisfied: the bound gates a warning, not the install.
    /// </summary>
    public static bool SatisfiesAppVersion(string? minAppVersion, string? currentVersion)
    {
        if (!Version.TryParse(minAppVersion, out var min)) return true;
        if (!Version.TryParse(currentVersion, out var current)) return true;
        return current >= min;
    }

    /// <summary>
    /// Comparison key for a packager or definition name: lower case, alphanumerics
    /// only, one trailing architecture suffix removed.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var core = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var suffix in new[] { "x64", "x86", "both" })
        {
            if (core.Length > suffix.Length && core.EndsWith(suffix, StringComparison.Ordinal))
                return core[..^suffix.Length];
        }
        return core;
    }

    /// <summary>
    /// Pack-side name aliases for applications the two catalogs name differently.
    /// Keyed and valued on normalized names.
    /// </summary>
    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.Ordinal)
    {
        ["teamsnew"] = "teams"
    };

    /// <summary>
    /// Resolves each manifest entry to definition base names: exact name match first,
    /// then the normalized key, which fans an arch-collapsed pack entry out to every
    /// arch variant of the definition. An entry matching nothing is unmatched.
    /// </summary>
    public static IconPackMapResult MapEntries(IEnumerable<IconPackEntry> entries, IEnumerable<string> definitionBaseNames)
    {
        var definitions = definitionBaseNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

        var byExact = definitions.ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);
        var byNormalized = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in definitions)
        {
            var key = NormalizeName(name);
            if (key.Length == 0) continue;
            if (!byNormalized.TryGetValue(key, out var bucket))
                byNormalized[key] = bucket = new List<string>();
            bucket.Add(name);
        }

        var mappings = new List<IconPackMapping>();
        var unmatched = new List<IconPackEntry>();

        foreach (var entry in entries)
        {
            var packagerName = string.IsNullOrWhiteSpace(entry.Packager)
                ? Path.GetFileNameWithoutExtension(entry.File ?? "")
                : entry.Packager;

            if (!string.IsNullOrWhiteSpace(packagerName) && byExact.TryGetValue(packagerName, out var exact))
            {
                mappings.Add(new IconPackMapping(entry, exact, Exact: true));
                continue;
            }

            var key = NormalizeName(packagerName);
            if (NameAliases.TryGetValue(key, out var aliased)) key = aliased;

            if (key.Length > 0 && byNormalized.TryGetValue(key, out var matches))
            {
                foreach (var match in matches)
                    mappings.Add(new IconPackMapping(entry, match, Exact: false));
                continue;
            }

            unmatched.Add(entry);
        }

        return new IconPackMapResult(mappings, unmatched);
    }

    /// <summary>Installed pack status read from the manifest kept beside the icons.</summary>
    public static IconPackStatus ReadStatus(string definitionsDirectory)
    {
        var iconsDirectory = IconsDirectory(definitionsDirectory);
        var manifest = ReadManifest(Path.Combine(iconsDirectory, "manifest.json"));
        if (manifest is null) return new IconPackStatus(false, "", 0);

        var installed = Directory.Exists(iconsDirectory)
            ? Directory.GetFiles(iconsDirectory, "*.png").Length
            : 0;
        return new IconPackStatus(true, manifest.PackVersion, installed);
    }

    /// <summary>Text for the status row: installed pack version and icon count, or the not-installed state.</summary>
    public static string StatusText(IconPackStatus status)
    {
        if (!status.Installed) return "Not installed — download the pack to use External icon definitions.";
        var version = string.IsNullOrWhiteSpace(status.PackVersion) ? "unknown" : status.PackVersion;
        var noun = status.IconCount == 1 ? "icon" : "icons";
        return $"Installed — pack {version}, {status.IconCount} {noun}.";
    }

    // ---- Install ----

    /// <summary>
    /// Downloads the latest pack release, verifies it against checksums.txt, and
    /// installs the mapped PNGs plus the manifest into
    /// <paramref name="definitionsDirectory"/>\Icons. Never throws.
    /// </summary>
    public async Task<IconPackInstallResult> InstallAsync(
        string? definitionsDirectory = null,
        string? appVersion = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        var definitions = string.IsNullOrWhiteSpace(definitionsDirectory)
            ? DefaultDefinitionsDirectory()
            : definitionsDirectory;
        var scratch = Path.Combine(Path.GetTempPath(), "packr-iconpack-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (!Directory.Exists(definitions))
                return new IconPackInstallResult(false, $"Definitions folder not found: {definitions}");

            var releaseJson = await FetchReleaseAsync(ct);
            if (releaseJson is null)
                return new IconPackInstallResult(false, "Icon pack release lookup failed; check network access to api.github.com.");

            var (tag, packUrl, sumsUrl) = SelectAssets(releaseJson);
            if (packUrl is null || sumsUrl is null)
                return new IconPackInstallResult(false, "The latest icon pack release carries no pack assets.");

            Directory.CreateDirectory(scratch);
            var zipPath = Path.Combine(scratch, PackAssetName);
            var sumsPath = Path.Combine(scratch, ChecksumAssetName);

            onLog?.Invoke($"Downloading icon pack {tag}...");
            await _downloads.DownloadAsync(packUrl, zipPath, retryCount: 1, onProgress: onLog, ct: ct);
            await _downloads.DownloadAsync(sumsUrl, sumsPath, retryCount: 1, onProgress: onLog, ct: ct);

            var expected = ParseChecksum(File.ReadAllText(sumsPath), PackAssetName);
            if (!VerifyChecksum(zipPath, expected))
                return new IconPackInstallResult(false, "Icon pack checksum did not match; nothing was installed.");

            var extracted = Path.Combine(scratch, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extracted, overwriteFiles: true);

            var manifest = ReadManifest(Path.Combine(extracted, "manifest.json"));
            if (manifest is null)
                return new IconPackInstallResult(false, "The icon pack carries no readable manifest.json.");

            var definitionNames = Directory.GetFiles(definitions, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();

            var map = MapEntries(manifest.Icons, definitionNames);

            var iconsDirectory = IconsDirectory(definitions);
            Directory.CreateDirectory(iconsDirectory);

            var copied = 0;
            foreach (var mapping in map.Mappings)
            {
                var source = Path.Combine(extracted, mapping.Entry.File ?? "");
                if (string.IsNullOrWhiteSpace(mapping.Entry.File) || !File.Exists(source)) continue;
                File.Copy(source, Path.Combine(iconsDirectory, mapping.DefinitionBaseName + ".png"), overwrite: true);
                copied++;
            }

            File.Copy(Path.Combine(extracted, "manifest.json"), Path.Combine(iconsDirectory, "manifest.json"), overwrite: true);

            var warning = SatisfiesAppVersion(manifest.MinAppVersion, appVersion)
                ? ""
                : $" Pack targets packr {manifest.MinAppVersion} or newer; installed anyway.";

            var message = $"Icon pack {tag} installed: {copied} icons for {map.MatchedEntries} packagers, "
                        + $"{map.Unmatched.Count} without a matching definition.{warning}";
            onLog?.Invoke(message);

            return new IconPackInstallResult(true, message, manifest.PackVersion,
                map.MatchedEntries, copied, map.Unmatched.Count);
        }
        catch (Exception ex)
        {
            return new IconPackInstallResult(false, $"Icon pack install failed: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    /// <summary>Release tag and the two asset download URLs; a missing asset yields null.</summary>
    public static (string Tag, string? PackUrl, string? SumsUrl) SelectAssets(string releaseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";

            string? pack = null, sums = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == PackAssetName) pack ??= url;
                    else if (name == ChecksumAssetName) sums ??= url;
                }
            }
            return (tag, pack, sums);
        }
        catch
        {
            return ("", null, null);
        }
    }

    private static async Task<string?> FetchReleaseAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Repository}/releases/latest";
        using var process = CurlProcess.Start(
            $"-L --silent --show-error --max-time 25 -A \"{UserAgent}\" \"{url}\"", url);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }
}
