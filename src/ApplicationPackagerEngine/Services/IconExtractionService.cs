using System.Drawing;
using System.Drawing.Imaging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Extracts application icons from installer payloads.
/// Uses 7z extraction to find the primary EXE, then reads its embedded icon.
/// Icons are resized to 32x32 PNG and base64-encoded for storage in definitions or cache.
/// </summary>
public sealed class IconExtractionService
{
    private readonly ArchiveExtractionService _archiveService;
    private readonly string _cacheDir;

    public IconExtractionService(ArchiveExtractionService archiveService, string? cacheDir = null)
    {
        _archiveService = archiveService;
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SRLPackr", "icons");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Extracts an icon from an installer file. For MSI/EXE, extracts the payload and reads
    /// the primary EXE's embedded icon. Returns a base64-encoded 32x32 PNG.
    /// </summary>
    public async Task<IconResult> ExtractAsync(string installerPath, string appId, CancellationToken ct = default)
    {
        var cachedPath = Path.Combine(_cacheDir, $"{appId}.png");
        if (File.Exists(cachedPath))
        {
            var cachedBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(cachedPath, ct));
            return new IconResult { Success = true, Base64Png = cachedBase64, CachedPath = cachedPath };
        }

        var ext = Path.GetExtension(installerPath).ToLowerInvariant();

        // For standalone EXEs, read icon directly
        if (ext == ".exe")
        {
            var directResult = ExtractFromExe(installerPath, cachedPath);
            if (directResult.Success)
                return directResult;
        }

        // For archives (MSI wrapped in EXE, NSIS, InnoSetup, etc.), extract and find primary EXE
        var tempDir = Path.Combine(Path.GetTempPath(), $"packr-icon-{Guid.NewGuid():N}");
        try
        {
            var extraction = await _archiveService.ExtractAsync(installerPath, tempDir, ct);
            if (!extraction.Success)
                return new IconResult { Success = false, ErrorMessage = "Failed to extract installer" };

            // Find the best candidate EXE (largest non-utility EXE)
            var exes = Directory.GetFiles(tempDir, "*.exe", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("$PLUGINSDIR", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(f).Equals("setup.exe", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(f).Equals("7z.exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToArray();

            foreach (var exe in exes)
            {
                var result = ExtractFromExe(exe, cachedPath);
                if (result.Success)
                    return result;
            }

            return new IconResult { Success = false, ErrorMessage = "No extractable icon found in installer" };
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Extracts icon from a specific EXE file, resizes to 32x32, saves as PNG.
    /// </summary>
    private static IconResult ExtractFromExe(string exePath, string outputPngPath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null)
                return new IconResult { Success = false, ErrorMessage = "No icon in EXE" };

            using var bitmap = icon.ToBitmap();
            using var resized = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(bitmap, 0, 0, 32, 32);
            }

            resized.Save(outputPngPath, ImageFormat.Png);

            var base64 = Convert.ToBase64String(File.ReadAllBytes(outputPngPath));
            return new IconResult
            {
                Success = true,
                Base64Png = base64,
                CachedPath = outputPngPath
            };
        }
        catch (Exception ex)
        {
            return new IconResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Gets the cached icon path for an app, or null if not cached.
    /// </summary>
    public string? GetCachedPath(string appId)
    {
        var path = Path.Combine(_cacheDir, $"{appId}.png");
        return File.Exists(path) ? path : null;
    }
}

public sealed class IconResult
{
    public bool Success { get; init; }
    public string? Base64Png { get; init; }
    public string? CachedPath { get; init; }
    public string? ErrorMessage { get; init; }
}
