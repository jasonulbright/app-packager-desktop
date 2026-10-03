using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// One icon extracted from an installer, already written to disk in a
/// MECM-compatible format.
/// </summary>
public sealed record InstallerIcon(string Path, int PixelSize, string Format, string Source);

/// <summary>
/// Reads icon resources out of a PE module through the resource directory.
/// The directory is parsed directly rather than asking the shell for a
/// handle, which reports whatever size was requested instead of the size the
/// resource actually carries.
/// </summary>
internal static class IconNative
{
    private const int RT_ICON = 3;
    private const int RT_GROUP_ICON = 14;
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNamesW(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

    private static byte[]? ResourceBytes(IntPtr hModule, IntPtr type, IntPtr name)
    {
        var info = FindResourceW(hModule, name, type);
        if (info == IntPtr.Zero) return null;
        var size = SizeofResource(hModule, info);
        if (size == 0) return null;
        var data = LoadResource(hModule, info);
        if (data == IntPtr.Zero) return null;
        var ptr = LockResource(data);
        if (ptr == IntPtr.Zero) return null;
        var buffer = new byte[size];
        Marshal.Copy(ptr, buffer, 0, (int)size);
        return buffer;
    }

    /// <summary>
    /// Builds a single-image .ico from the largest RT_ICON of the module's
    /// first icon group. Null when the module carries no icon group.
    /// </summary>
    internal static byte[]? ExtractLargestIcon(string path, out int pixelSize)
    {
        pixelSize = 0;
        var hModule = LoadLibraryExW(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (hModule == IntPtr.Zero) return null;
        try
        {
            var firstGroup = IntPtr.Zero;
            EnumResNameProc collect = (_, _, n, _) => { firstGroup = n; return false; };
            EnumResourceNamesW(hModule, RT_GROUP_ICON, collect, IntPtr.Zero);
            GC.KeepAlive(collect);
            if (firstGroup == IntPtr.Zero) return null;

            var group = ResourceBytes(hModule, RT_GROUP_ICON, firstGroup);
            if (group is null) return null;

            return IconExtractionService.BuildIcoFromGroup(
                group,
                id => ResourceBytes(hModule, RT_ICON, (IntPtr)id),
                out pixelSize);
        }
        finally
        {
            FreeLibrary(hModule);
        }
    }
}

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
        _cacheDir = cacheDir ?? Path.Combine(AppDataPaths.LegacyRoot, "icons");
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

    // ---- Stage-time icon placement ----

    /// <summary>
    /// Places app-icon.* in a stage folder and records the file name on the
    /// manifest so packaging can find it inside the content folder.
    ///
    /// An icon pack entry Definitions\Icons\&lt;definition file name&gt;.ico|png
    /// wins for every IconSource: pack icons are at least 256x256, extracted
    /// installer icons are often 64x64 or 128x128. Without one, Installer
    /// extracts from the staged installer the manifest names (from the
    /// ZipPayload icon entry for a ZIP payload), External reports the missing
    /// file, and None is a no-op. Never throws: an icon is decoration, and a
    /// failed extraction must not fail a stage.
    /// </summary>
    public static void StageIcon(
        Models.AppDefinition definition, string stageRoot,
        Models.StageManifest manifest, Action<string>? onLog = null)
    {
        var iconSource = NormalizeIconSource(definition.IconSource);

        try
        {
            if (StagePackIcon(definition, stageRoot, manifest, onLog)) return;
            if (iconSource == "None") return;

            if (iconSource == "External")
            {
                onLog?.Invoke($"IconSource External but no Definitions\\Icons\\{ExternalIconBaseName(definition)}.ico|png found; continuing without an icon.");
                return;
            }

            if (string.IsNullOrWhiteSpace(manifest.InstallerFile))
            {
                onLog?.Invoke("IconSource Installer but the manifest declares no InstallerFile; continuing without an icon.");
                return;
            }

            var installerPath = Path.Combine(stageRoot, manifest.InstallerFile);
            if (!File.Exists(installerPath))
            {
                onLog?.Invoke($"IconSource Installer but {manifest.InstallerFile} is not in the stage folder; continuing without an icon.");
                return;
            }

            var iconEntry = definition.ZipPayload?.IconEntry;
            if (!string.IsNullOrWhiteSpace(iconEntry)
                && installerPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                StageZipEntryIcon(installerPath, iconEntry, stageRoot, manifest, onLog);
                return;
            }

            var result = ExtractInstallerIcon(installerPath, Path.Combine(stageRoot, "app-icon.ico"), MinimumDimension, onLog);
            if (result is null) return;
            manifest.Icon = Path.GetFileName(result.Path);
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"Icon staging failed          : {ex.Message}");
        }
    }

    /// <summary>
    /// The ZIP is not an executable, so the icon comes from the launcher
    /// inside it, extracted to a temporary file that is always removed.
    /// </summary>
    private static void StageZipEntryIcon(
        string zipPath, string entryName, string stageRoot,
        Models.StageManifest manifest, Action<string>? onLog)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"packr-zipicon-{Guid.NewGuid():N}.exe");
        try
        {
            if (!ZipPayload.ExtractEntry(zipPath, entryName, temp))
            {
                onLog?.Invoke($"IconSource Installer but the ZIP has no {entryName}; continuing without an icon.");
                return;
            }
            var result = ExtractInstallerIcon(temp, Path.Combine(stageRoot, "app-icon.ico"), MinimumDimension, onLog);
            if (result is not null) manifest.Icon = Path.GetFileName(result.Path);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Copies the icon pack entry named after the definition file, .ico before
    /// .png. Only an exact base-name match counts, so a pack entry for a
    /// longer name that shares the prefix is not taken.
    /// </summary>
    private static bool StagePackIcon(
        Models.AppDefinition definition, string stageRoot,
        Models.StageManifest manifest, Action<string>? onLog)
    {
        var iconsDirectory = ExternalIconsDirectory(definition);
        var baseName = ExternalIconBaseName(definition);
        var candidate = new[] { ".ico", ".png" }
            .Select(ext => Path.Combine(iconsDirectory, baseName + ext))
            .FirstOrDefault(File.Exists);
        if (candidate is null) return false;

        var destination = Path.Combine(stageRoot, "app-icon" + Path.GetExtension(candidate).ToLowerInvariant());
        File.Copy(candidate, destination, true);
        manifest.Icon = Path.GetFileName(destination);
        onLog?.Invoke($"Staged icon pack icon        : {manifest.Icon}");
        return true;
    }

    /// <summary>
    /// The Icons folder beside the definition file, falling back to the
    /// Definitions folder shipped next to the executable when the definition
    /// carries no source path.
    /// </summary>
    public static string ExternalIconsDirectory(Models.AppDefinition definition)
    {
        var definitionDirectory = string.IsNullOrWhiteSpace(definition.SourcePath)
            ? Path.Combine(AppContext.BaseDirectory, "Definitions")
            : Path.GetDirectoryName(Path.GetFullPath(definition.SourcePath))!;
        return Path.Combine(definitionDirectory, "Icons");
    }

    /// <summary>
    /// External icon file name for a definition: its .json file's base name,
    /// which is the human-readable definition name. Id is a content hash and
    /// would not be recognizable in the Icons folder.
    /// </summary>
    public static string ExternalIconBaseName(Models.AppDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.SourcePath)
            ? definition.Id
            : Path.GetFileNameWithoutExtension(definition.SourcePath);

    /// <summary>
    /// An absent, blank, or unrecognized IconSource is None, which keeps every
    /// untagged definition on the pre-icon path.
    /// </summary>
    public static string NormalizeIconSource(string? value) =>
        value?.Trim() switch
        {
            "Installer" => "Installer",
            "External" => "External",
            _ => "None"
        };

    // ---- True-size installer icon extraction ----

    /// <summary>
    /// Smallest largest-frame size accepted. Setup-stub icons top out at 32px
    /// while real application icons carry a 48px or larger frame; distinguishing
    /// them by content is not reliable, so the floor plus the per-definition
    /// IconSource opt-in is the control.
    /// </summary>
    public const int MinimumDimension = 32;

    /// <summary>Configuration Manager rejects an icon image wider than this.</summary>
    public const int MaxDimension = 512;

    /// <summary>Configuration Manager rejects an icon image file above this size.</summary>
    public const int MaxIconBytes = 256 * 1024;

    /// <summary>
    /// Delays before each attempt at loading a module's resources.
    /// LoadLibraryEx fails with no error while an on-access scan still holds a
    /// freshly written file, which would silently degrade the very first probe
    /// of a just-downloaded payload to the 32px fallback.
    /// </summary>
    private static readonly int[] TransientLoadDelaysMs = [0, 400, 1200];

    /// <summary>
    /// Builds a single-image .ico from an RT_GROUP_ICON directory, selecting
    /// the largest entry and breaking ties on colour depth. Returns null when
    /// the directory is malformed or its chosen image cannot be read.
    /// </summary>
    public static byte[]? BuildIcoFromGroup(byte[] group, Func<int, byte[]?> imageLookup, out int pixelSize)
    {
        pixelSize = 0;
        if (group.Length < 6) return null;

        int count = BitConverter.ToUInt16(group, 4);
        if (count <= 0 || group.Length < 6 + (count * 14)) return null;

        var bestIndex = -1;
        var bestSize = -1;
        var bestBits = -1;
        for (var i = 0; i < count; i++)
        {
            var off = 6 + (i * 14);
            // A zero width/height byte encodes 256 in the icon directory.
            int w = group[off] == 0 ? 256 : group[off];
            int h = group[off + 1] == 0 ? 256 : group[off + 1];
            var dim = Math.Max(w, h);
            int bits = BitConverter.ToUInt16(group, off + 6);
            if (dim > bestSize || (dim == bestSize && bits > bestBits))
            {
                bestSize = dim;
                bestBits = bits;
                bestIndex = i;
            }
        }
        if (bestIndex < 0) return null;

        var bestOff = 6 + (bestIndex * 14);
        int iconId = BitConverter.ToUInt16(group, bestOff + 12);
        var image = imageLookup(iconId);
        if (image is null || image.Length == 0) return null;

        // ICONDIR + one ICONDIRENTRY (16 bytes, versus the group's 14) then the image.
        var ico = new byte[22 + image.Length];
        ico[2] = 1;
        ico[4] = 1;
        ico[6] = group[bestOff];
        ico[7] = group[bestOff + 1];
        ico[8] = group[bestOff + 2];
        ico[10] = group[bestOff + 4];
        ico[11] = group[bestOff + 5];
        ico[12] = group[bestOff + 6];
        ico[13] = group[bestOff + 7];
        BitConverter.GetBytes(image.Length).CopyTo(ico, 14);
        BitConverter.GetBytes(22).CopyTo(ico, 18);
        image.CopyTo(ico, 22);

        pixelSize = bestSize;
        return ico;
    }

    /// <summary>
    /// Largest image dimension declared by .ico file bytes, or 0 when the
    /// buffer is not an icon directory.
    /// </summary>
    public static int GetIconBytesDimension(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 22) return 0;
        if (BitConverter.ToUInt16(bytes, 0) != 0 || BitConverter.ToUInt16(bytes, 2) != 1) return 0;

        int count = BitConverter.ToUInt16(bytes, 4);
        if (count <= 0 || bytes.Length < 6 + (count * 16)) return 0;

        var best = 0;
        for (var i = 0; i < count; i++)
        {
            var off = 6 + (i * 16);
            int w = bytes[off] == 0 ? 256 : bytes[off];
            int h = bytes[off + 1] == 0 ? 256 : bytes[off + 1];
            best = Math.Max(best, Math.Max(w, h));
        }
        return best;
    }

    /// <summary>
    /// Extracts an application icon from an installer into a MECM-compatible
    /// file. PE inputs yield the largest image of the first icon group; MSI
    /// inputs come from the Icon table, preferring the ARPPRODUCTICON stream.
    ///
    /// OutputPath's extension selects the format: .png converts, anything else
    /// writes the icon bytes verbatim. Returns null when no icon exists, the
    /// largest image is under <paramref name="minimumSize"/>, or the result
    /// still exceeds the 256 KB image limit after re-encoding.
    /// </summary>
    public static InstallerIcon? ExtractInstallerIcon(
        string installerPath, string outputPath,
        int minimumSize = MinimumDimension, Action<string>? onLog = null)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Installer not found", installerPath);

        var full = Path.GetFullPath(installerPath);
        var extension = Path.GetExtension(full).ToLowerInvariant();

        byte[]? bytes;
        int pixelSize;
        string source;

        if (extension == ".msi")
            (bytes, pixelSize, source) = ReadMsiIcon(full, onLog);
        else
            (bytes, pixelSize, source) = ReadPeIcon(full, onLog);

        if (bytes is null || bytes.Length == 0)
        {
            onLog?.Invoke($"No icon resource found       : {Path.GetFileName(full)}");
            return null;
        }

        return MaterializeIcon(bytes, pixelSize, source, outputPath, minimumSize,
            Path.GetFileName(full), onLog);
    }

    /// <summary>
    /// Applies the size floor, writes the icon in the format the output
    /// extension asks for, and enforces the 256 KB image cap. Null when the
    /// image is under the floor or still too large after re-encoding.
    /// </summary>
    public static InstallerIcon? MaterializeIcon(
        byte[] bytes, int pixelSize, string source, string outputPath,
        int minimumSize = MinimumDimension, string sourceName = "", Action<string>? onLog = null)
    {
        if (pixelSize < minimumSize)
        {
            onLog?.Invoke($"Icon rejected (too small)    : {pixelSize}px from {sourceName} (floor {minimumSize}px)");
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        (outputPath, pixelSize) = WriteIconFile(bytes, outputPath, pixelSize);
        if (new FileInfo(outputPath).Length > MaxIconBytes)
        {
            var capped = EnforceSizeCap(outputPath, pixelSize);
            if (capped is null)
            {
                onLog?.Invoke($"Icon rejected (too large)    : {Path.GetFileName(outputPath)} exceeds the 256 KB image limit after re-encoding");
                TryDelete(outputPath);
                return null;
            }
            (outputPath, pixelSize) = capped.Value;
        }

        var format = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        onLog?.Invoke($"Extracted installer icon     : {Path.GetFileName(outputPath)} ({pixelSize}px, {source})");
        return new InstallerIcon(outputPath, pixelSize, format, source);
    }

    private static (byte[]? Bytes, int PixelSize, string Source) ReadPeIcon(string path, Action<string>? onLog)
    {
        foreach (var delay in TransientLoadDelaysMs)
        {
            if (delay > 0) Thread.Sleep(delay);
            var bytes = IconNative.ExtractLargestIcon(path, out var size);
            if (bytes is not null) return (bytes, size, "PeResource");
        }

        try
        {
            using var associated = Icon.ExtractAssociatedIcon(path);
            if (associated is null) return (null, 0, "");
            using var stream = new MemoryStream();
            associated.Save(stream);
            var bytes = stream.ToArray();
            return (bytes, GetIconBytesDimension(bytes), "ExtractAssociatedIcon");
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"Icon fallback failed         : {ex.Message}");
            return (null, 0, "");
        }
    }

    private static (byte[]? Bytes, int PixelSize, string Source) ReadMsiIcon(string path, Action<string>? onLog)
    {
        var bytes = MsiIconReader.Read(path, onLog);
        if (bytes is null) return (null, 0, "");

        var pixelSize = GetIconBytesDimension(bytes);
        if (pixelSize > 0) return (bytes, pixelSize, "MsiIconTable");

        // An Icon table row may hold an EXE rather than an .ico; the PE reader
        // handles that shape.
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".exe");
        try
        {
            File.WriteAllBytes(temp, bytes);
            var (peBytes, peSize, _) = ReadPeIcon(temp, onLog);
            if (peBytes is not null) return (peBytes, peSize, "MsiIconTablePe");
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"MSI icon PE read failed      : {ex.Message}");
        }
        finally
        {
            TryDelete(temp);
        }
        return (null, 0, "");
    }

    /// <summary>
    /// Writes the icon bytes at outputPath, converting to PNG when the output
    /// extension asks for it or when the image is wider than the MECM ceiling.
    /// </summary>
    private static (string Path, int PixelSize) WriteIconFile(byte[] bytes, string outputPath, int pixelSize)
    {
        var wantsPng = Path.GetExtension(outputPath).Equals(".png", StringComparison.OrdinalIgnoreCase);
        if (!wantsPng && pixelSize <= MaxDimension)
        {
            File.WriteAllBytes(outputPath, bytes);
            return (outputPath, pixelSize);
        }

        var target = Math.Min(pixelSize, MaxDimension);
        var pngPath = wantsPng ? outputPath : Path.ChangeExtension(outputPath, ".png");
        var frame = PngFrame(bytes);

        // A 256px icon frame is normally stored PNG-compressed rather than as a
        // DIB, and System.Drawing.Icon cannot decode that shape; when it is
        // already the wanted size the frame is copied out byte for byte.
        if (frame is not null && pixelSize <= MaxDimension)
        {
            File.WriteAllBytes(pngPath, frame);
            return (pngPath, target);
        }

        using var stream = new MemoryStream(frame ?? bytes);
        using var sourceImage = frame is not null
            ? Image.FromStream(stream)
            : DecodeIcon(stream, target);
        using var bitmap = new Bitmap(sourceImage, target, target);
        bitmap.Save(pngPath, ImageFormat.Png);
        if (!string.Equals(pngPath, outputPath, StringComparison.OrdinalIgnoreCase)) TryDelete(outputPath);
        return (pngPath, target);
    }

    /// <summary>
    /// Re-encodes an oversized icon file as a single PNG, downscaling until it
    /// fits. An .ico carrying every frame can exceed the 256 KB image limit
    /// even when its largest frame is small. Null when no size fits.
    /// </summary>
    private static (string Path, int PixelSize)? EnforceSizeCap(string outputPath, int pixelSize)
    {
        var pngPath = Path.ChangeExtension(outputPath, ".png");
        var fileBytes = File.ReadAllBytes(outputPath);
        var frame = PngFrame(fileBytes);
        var isPng = fileBytes.Length > 1 && fileBytes[0] == 0x89 && fileBytes[1] == 0x50;

        var targets = new[] { pixelSize, MaxDimension, 256, 128, 64 }
            .Where(t => t <= pixelSize)
            .Distinct()
            .OrderByDescending(t => t);

        foreach (var target in targets)
        {
            try
            {
                using var stream = new MemoryStream(frame ?? fileBytes);
                using var sourceImage = frame is not null || isPng
                    ? Image.FromStream(stream)
                    : DecodeIcon(stream, target);
                using var bitmap = new Bitmap(sourceImage, target, target);
                bitmap.Save(pngPath, ImageFormat.Png);
            }
            catch
            {
                continue;
            }

            if (new FileInfo(pngPath).Length <= MaxIconBytes)
            {
                if (!string.Equals(pngPath, outputPath, StringComparison.OrdinalIgnoreCase)) TryDelete(outputPath);
                return (pngPath, target);
            }
        }
        if (!string.Equals(pngPath, outputPath, StringComparison.OrdinalIgnoreCase)) TryDelete(pngPath);
        return null;
    }

    private static Bitmap DecodeIcon(Stream stream, int target)
    {
        using var icon = new Icon(stream, target, target);
        return icon.ToBitmap();
    }

    /// <summary>
    /// The PNG-compressed image of a single-frame .ico, or null when the
    /// buffer is not an .ico whose first image is PNG-compressed.
    /// </summary>
    private static byte[]? PngFrame(byte[] bytes)
    {
        if (bytes.Length < 22 || bytes[0] != 0 || bytes[2] != 1) return null;
        var offset = BitConverter.ToInt32(bytes, 18);
        if (offset <= 0 || bytes.Length <= offset + 8) return null;
        if (bytes[offset] != 0x89 || bytes[offset + 1] != 0x50 ||
            bytes[offset + 2] != 0x4E || bytes[offset + 3] != 0x47) return null;
        return bytes[offset..];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed class IconResult
{
    public bool Success { get; init; }
    public string? Base64Png { get; init; }
    public string? CachedPath { get; init; }
    public string? ErrorMessage { get; init; }
}
