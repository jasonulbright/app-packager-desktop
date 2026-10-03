using System.Diagnostics;
using System.Xml.Linq;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Scrapes the Office CDN VersionDescriptor.xml (inside a CAB file) to determine
/// the latest M365 Apps version for a given channel and architecture.
///
/// Pattern field specifies the architecture version attribute:
///   "I640Version" — 64-bit version
///   "I320Version" — 32-bit version
///
/// The URL must point to the channel-specific CAB file, e.g.:
///   https://officecdn.microsoft.com/pr/{ChannelGuid}/Office/Data/v64.cab
/// </summary>
public sealed class CdnCabScraper : IVersionScraper
{
    public string Type => "CdnCab";

    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "SRLPackr-CdnCabCache");

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        Directory.CreateDirectory(CacheDir);

        var cabFileName = $"cdn-{Math.Abs(config.Url.GetHashCode()):x8}.cab";
        var cabPath = Path.Combine(CacheDir, cabFileName);
        var extractDir = Path.Combine(CacheDir, Path.GetFileNameWithoutExtension(cabFileName));

        // Download CAB
        await DownloadAsync(config.Url, cabPath, ct);
        if (!File.Exists(cabPath)) return null;

        // Extract VersionDescriptor.xml using expand.exe
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        var expandResult = await RunProcessAsync(
            @"C:\Windows\System32\expand.exe",
            $"\"{cabPath}\" -F:VersionDescriptor.xml \"{extractDir}\"",
            ct);
        if (!expandResult) return null;

        var vdPath = Path.Combine(extractDir, "VersionDescriptor.xml");
        if (!File.Exists(vdPath)) return null;

        // Parse XML and extract version
        try
        {
            var xml = XDocument.Load(vdPath);
            var available = xml.Root?.Element("Available");
            if (available is null) return null;

            var versionAttr = config.Pattern switch
            {
                "I320Version" => available.Attribute("I320Version"),
                _ => available.Attribute("I640Version")
            };

            var version = versionAttr?.Value;

            // Strip "16.0." prefix — display version is just the build portion
            if (version is not null && version.StartsWith("16.0."))
                version = version["16.0.".Length..];

            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Cleanup
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
            try { if (File.Exists(cabPath)) File.Delete(cabPath); } catch { }
        }
    }

    private static async Task DownloadAsync(string url, string outFile, CancellationToken ct)
    {
        using var process = CurlProcess.Start(
            ["-L", "--fail", "--silent", "--show-error", "--max-time", "30", "-o", outFile], url);

        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(35), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
        }
    }

    private static async Task<bool> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return false;
        }

        return process.ExitCode == 0;
    }
}
