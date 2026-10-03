using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Resolves the newest Dell Command | Update Windows Universal Application (WIN64)
/// release from Dell's client catalog.
///
/// The published CatalogPC.cab nests a second cab that carries UTF-16 XML, so two
/// expand passes are required; expand.exe writes a file rather than a directory
/// when the destination does not exist, so each pass gets its own directory.
///
/// Dell publishes several components at one vendorVersion that differ only in the
/// models they list, so ties are broken by release date and then by supported-model
/// count: the broadest package installs across a mixed fleet.
///
/// Derives from HttpRegexScraper so that download-URL resolution treats it as a
/// regex scraper; every fetch and extraction path is overridden.
/// </summary>
public sealed class DellCatalogScraper : HttpRegexScraper
{
    public override string Type => "DellCatalog";

    private const string DefaultCatalogUrl = "https://downloads.dell.com/catalog/CatalogPC.cab";
    private const string CatalogBaseUrl = "https://downloads.dell.com";
    private const string DefaultComponentPattern = "Dell-Command-Update-Windows-Universal-Application[^\"]*WIN64";

    public override async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        var (version, _) = await ResolveAsync(config, ct);
        return version;
    }

    public override async Task<(string? Version, string? DownloadUrl)> GetVersionAndDownloadUrlAsync(
        VersionScraperConfig config, CancellationToken ct = default)
        => await ResolveAsync(config, ct);

    private static async Task<(string? Version, string? DownloadUrl)> ResolveAsync(
        VersionScraperConfig config, CancellationToken ct)
    {
        var catalogUrl = string.IsNullOrWhiteSpace(config.Url) ? DefaultCatalogUrl : config.Url;
        var componentPattern = string.IsNullOrWhiteSpace(config.Pattern) ? DefaultComponentPattern : config.Pattern;

        var workRoot = Path.Combine(Path.GetTempPath(), "packr-dell-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workRoot);
            var xmlPath = await DownloadAndExpandCatalogAsync(catalogUrl, workRoot, ct);
            if (xmlPath is null) return (null, null);

            var xml = await File.ReadAllTextAsync(xmlPath, Encoding.Unicode, ct);
            var best = SelectBestComponent(xml, componentPattern);
            if (best is null) return (null, null);

            return (best.Value.Version, $"{CatalogBaseUrl}/{best.Value.Path.Replace('\\', '/')}");
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Picks the winning SoftwareComponent: highest vendorVersion, then newest
    /// release date, then the widest supported-model list.
    /// </summary>
    public static (string Version, string Path)? SelectBestComponent(string xml, string componentPattern)
    {
        var nodePattern = $"(?s)<SoftwareComponent[^>]*path=\"[^\"]*{componentPattern}[^\"]*\".*?</SoftwareComponent>";

        (string Version, string Path)? best = null;
        LooseVersion? bestVersion = null;
        DateTimeOffset bestDate = DateTimeOffset.MinValue;
        var bestModels = -1;

        foreach (Match m in Regex.Matches(xml, nodePattern))
        {
            XElement node;
            try { node = XElement.Parse(m.Value); }
            catch { continue; }

            var version = (string?)node.Attribute("vendorVersion");
            var path = (string?)node.Attribute("path");
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(path)) continue;

            var parsed = LooseVersion.TryParse(version);
            if (parsed is null) continue;

            DateTimeOffset.TryParse(
                (string?)node.Attribute("releaseDate"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var releaseDate);

            var models = node.Descendants()
                .Count(e => e.Name.LocalName.Equals("Model", StringComparison.OrdinalIgnoreCase));

            var better = bestVersion is null
                || parsed.CompareTo(bestVersion) > 0
                || (parsed.CompareTo(bestVersion) == 0
                    && (releaseDate > bestDate || (releaseDate == bestDate && models > bestModels)));

            if (!better) continue;

            best = (version!, path!);
            bestVersion = parsed;
            bestDate = releaseDate;
            bestModels = models;
        }

        return best;
    }

    private static async Task<string?> DownloadAndExpandCatalogAsync(string catalogUrl, string workRoot, CancellationToken ct)
    {
        var cabPath = Path.Combine(workRoot, "CatalogPC.cab");
        if (!await DownloadAsync(catalogUrl, cabPath, ct)) return null;

        var stage1 = Path.Combine(workRoot, "stage1");
        var stage2 = Path.Combine(workRoot, "stage2");
        Directory.CreateDirectory(stage1);
        Directory.CreateDirectory(stage2);

        if (!await ExpandAsync(cabPath, stage1, ct)) return null;
        var inner = LargestFile(stage1);
        if (inner is null) return null;

        if (IsCabinet(inner))
        {
            if (!await ExpandAsync(inner, stage2, ct)) return null;
            inner = LargestFile(stage2);
            if (inner is null) return null;
        }

        return inner;
    }

    private static string? LargestFile(string directory)
        => new DirectoryInfo(directory).GetFiles()
            .OrderByDescending(f => f.Length)
            .Select(f => f.FullName)
            .FirstOrDefault();

    private static bool IsCabinet(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == 4
            && signature[0] == 0x4D && signature[1] == 0x53 && signature[2] == 0x43 && signature[3] == 0x46;
    }

    private static async Task<bool> ExpandAsync(string cabPath, string destination, CancellationToken ct)
    {
        var expand = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");

        var exitCode = await RunAsync(expand, $"\"{cabPath}\" -F:* \"{destination}\"", TimeSpan.FromMinutes(3), ct);
        return exitCode == 0;
    }

    private static async Task<bool> DownloadAsync(string url, string outFile, CancellationToken ct)
    {
        using var process = CurlProcess.Start(
            ["-L", "--silent", "--show-error", "--max-time", "180", "-A", UserAgent, "-o", outFile], url);
        var exitCode = await WaitAsync(process, TimeSpan.FromMinutes(4), ct);
        return exitCode == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0;
    }

    private static async Task<int> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
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
        return await WaitAsync(process, timeout, ct);
    }

    private static async Task<int> WaitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout, ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return -1;
        }

        await stdoutTask;
        return process.ExitCode;
    }
}
