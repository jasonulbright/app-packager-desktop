using System.Diagnostics;
using System.IO.Compression;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Extracts version by downloading the installer and reading file properties.
/// Used for apps with no version API — the installer itself is the only version source.
///
/// Pattern field specifies the version source:
///   "FileVersion"       — FileVersionInfo.FileVersion (EXE)
///   "ProductVersion"    — FileVersionInfo.ProductVersion (EXE/bootstrapper)
///   "MsiProductVersion" — MSI ProductVersion property via COM
///
/// Downloaded files are cached in %TEMP%\SRLPackr-FileVersionCache\ so that
/// Stage can reuse the already-downloaded installer instead of re-downloading.
/// </summary>
public sealed class FileVersionScraper : IVersionScraper
{
    public string Type => "FileVersion";

    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "SRLPackr-FileVersionCache");

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        Directory.CreateDirectory(CacheDir);

        // Derive a stable filename from the URL
        var fileName = ResolveFileName(config.Url);
        var cachedPath = Path.Combine(CacheDir, fileName);

        // Always re-download (these URLs serve "latest" with no version in path)
        await DownloadAsync(config.Url, cachedPath, ct);
        if (!File.Exists(cachedPath)) return null;

        var version = config.Pattern.ToUpperInvariant() switch
        {
            "FILEVERSION" => ReadExeVersion(cachedPath, useFileVersion: true),
            "PRODUCTVERSION" => ReadExeVersion(cachedPath, useFileVersion: false),
            "MSIPRODUCTVERSION" => ReadMsiVersion(cachedPath),
            "MSIXMANIFEST" => ReadMsixManifestVersion(cachedPath),
            _ => ReadExeVersion(cachedPath, useFileVersion: false) // default to ProductVersion
        };

        return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    /// <summary>
    /// Returns the cached installer path if the file exists, or null.
    /// Called by StagingService to reuse the downloaded file.
    /// </summary>
    public static string? GetCachedPath(string url)
    {
        var fileName = ResolveFileName(url);
        var path = Path.Combine(CacheDir, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string ReadExeVersion(string exePath, bool useFileVersion)
    {
        var info = FileVersionInfo.GetVersionInfo(exePath);
        var version = useFileVersion ? info.FileVersion : info.ProductVersion;

        if (string.IsNullOrWhiteSpace(version))
            version = useFileVersion ? info.ProductVersion : info.FileVersion;

        if (string.IsNullOrWhiteSpace(version) && info.FileMajorPart > 0)
            version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";

        return version ?? "";
    }

    private static string ReadMsixManifestVersion(string msixPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(msixPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return "";

            using var reader = new StreamReader(entry.Open());
            var xml = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
            var ns = xml.Root?.GetDefaultNamespace();
            var identity = ns is not null
                ? xml.Root?.Element(ns + "Identity")
                : xml.Root?.Element("Identity");
            return identity?.Attribute("Version")?.Value ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadMsiVersion(string msiPath)
    {
        try
        {
            var service = new Services.MsiInspectionService();
            var props = service.ReadProperties(msiPath);
            return props.ProductVersion;
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var pathFile = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(pathFile) && pathFile.Contains('.'))
                return pathFile;
        }
        catch { }

        // Fallback: hash-based filename
        var hash = Math.Abs(url.GetHashCode()).ToString("x8");
        return $"download-{hash}.tmp";
    }

    private static async Task DownloadAsync(string url, string outFile, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"-L --fail --silent --show-error --max-time 120 -o \"{outFile}\" " +
                        $"-A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64)\" \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(130), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return;
        }

        if (process.ExitCode != 0)
        {
            // Clean up partial download
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        }
    }
}
