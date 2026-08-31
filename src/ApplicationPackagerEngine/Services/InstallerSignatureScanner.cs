using System.IO.Compression;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Identifies the installer engine of an ad-hoc file by extension, archive layout,
/// and binary signature. Unlike <see cref="ArchiveExtractionService"/> this needs no
/// external 7z.exe — it reads the head of the file directly, so it works on machines
/// without 7-Zip and is deterministic enough to unit test.
/// </summary>
public static class InstallerSignatureScanner
{
    /// <summary>Bytes read from the head of the file when scanning for engine markers.</summary>
    public const int ScanWindowBytes = 4 * 1024 * 1024;

    private static readonly string[] SquirrelMarkers =
    [
        "SquirrelTemp", "squirrel-install", "squirrel-updated",
        "squirrel-uninstall", "squirrel-firstrun", "squirrel-obsolete"
    ];

    /// <summary>
    /// Returns the engine name: NativeMSI, MSP, NSIS, InnoSetup, InstallShield, WixBurn,
    /// AdvancedInstaller, BitRock, Squirrel, SevenZipSfx, WinRarSfx, MSIX, MsixBundle,
    /// Chocolatey, NuGet, Intunewin, PsadtV3, PsadtV4, or Unknown.
    /// </summary>
    public static string Identify(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Installer not found", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".msi") return "NativeMSI";
        if (ext == ".msp") return "MSP";

        var byLayout = IdentifyByArchiveLayout(path, ext);
        if (byLayout is not null) return byLayout;

        return IdentifyBySignature(path);
    }

    private static string? IdentifyByArchiveLayout(string path, string ext)
    {
        switch (ext)
        {
            case ".nupkg":
                if (!TryReadEntryNames(path, out var nupkgEntries)) return null;
                if (!nupkgEntries.Any(e => IsRootEntry(e) && e.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)))
                    return null;
                return nupkgEntries.Any(e => LeafOf(e).StartsWith("chocolatey", StringComparison.OrdinalIgnoreCase)
                                          && e.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                    ? "Chocolatey"
                    : "NuGet";

            case ".intunewin":
                if (!TryReadEntryNames(path, out var intuneEntries)) return null;
                return intuneEntries.Any(e => e.Equals("IntuneWinPackage/Metadata/Detection.xml", StringComparison.OrdinalIgnoreCase))
                    ? "Intunewin"
                    : null;

            case ".msixbundle":
            case ".appxbundle":
                if (!TryReadEntryNames(path, out var bundleEntries)) return null;
                return bundleEntries.Any(e => e.Equals("AppxMetadata/AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase))
                    ? "MsixBundle"
                    : null;

            case ".msix":
            case ".appx":
                if (!TryReadEntryNames(path, out var msixEntries)) return null;
                return msixEntries.Any(e => e.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase))
                    ? "MSIX"
                    : null;

            case ".zip":
                if (!TryReadEntryNames(path, out var zipEntries)) return null;
                // v4 is tested first: a compatibility layout can carry both the v4
                // module and a legacy Deploy-Application.ps1.
                if (zipEntries.Any(e => LeafOf(e) is "PSAppDeployToolkit.psm1" or "PSAppDeployToolkit.psd1" or "Invoke-AppDeployToolkit.ps1"))
                    return "PsadtV4";
                if (zipEntries.Any(e => LeafOf(e) == "Deploy-Application.ps1")
                    && zipEntries.Any(e => LeafOf(e) == "AppDeployToolkitMain.ps1"))
                    return "PsadtV3";
                return "ZIP";

            default:
                return null;
        }
    }

    private static string IdentifyBySignature(string path)
    {
        var bytes = ReadHead(path, ScanWindowBytes);
        var ascii = Encoding.ASCII.GetString(bytes);
        // Modern Windows-native Squirrel builds keep their marker strings as
        // UTF-16LE; the ASCII view misses them entirely, so both views are scanned.
        var wide = Encoding.Unicode.GetString(bytes);

        // OLE compound document — an MSI wearing an .exe extension.
        if (bytes.Length >= 4 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0)
            return "NativeMSI";

        // Squirrel before NSIS: Squirrel bootstrappers are not NSIS-built, and
        // ordering guards against a future marker collision.
        var squirrelHits = SquirrelMarkers.Count(m =>
            ascii.Contains(m, StringComparison.Ordinal) || wide.Contains(m, StringComparison.Ordinal));
        var hasUpdateExe = ascii.Contains("Update.exe", StringComparison.Ordinal)
                        || wide.Contains("Update.exe", StringComparison.Ordinal);
        // Two distinct markers, or one plus Update.exe. A lone "Squirrel" string
        // is not enough — it appears in unrelated binaries.
        if (squirrelHits >= 2 || (squirrelHits >= 1 && hasUpdateExe))
            return "Squirrel";

        if (ascii.Contains("WixBundleManifest", StringComparison.Ordinal)
            || ascii.Contains(".wixburn", StringComparison.Ordinal))
            return "WixBurn";

        if (ascii.Contains("NullsoftInst", StringComparison.Ordinal)
            || ascii.Contains("Nullsoft.NSIS", StringComparison.Ordinal))
            return "NSIS";

        // NSIS firstheader magic, little-endian DEADBEEF. Only the first 64 KB
        // carries it; scanning further produces false positives on compressed data.
        if (IndexOfBytes(bytes, [0xEF, 0xBE, 0xAD, 0xDE], Math.Min(bytes.Length, 64 * 1024)) >= 0)
            return "NSIS";

        if (ascii.Contains("Inno Setup", StringComparison.Ordinal)) return "InnoSetup";
        if (ascii.Contains("InstallShield", StringComparison.Ordinal)) return "InstallShield";
        if (ascii.Contains("Advanced Installer", StringComparison.Ordinal)) return "AdvancedInstaller";
        if (ascii.Contains("BitRock", StringComparison.Ordinal)) return "BitRock";

        if (IndexOfBytes(bytes, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], bytes.Length) >= 0
            || ascii.Contains("!@InstallEnd@!", StringComparison.Ordinal))
            return "SevenZipSfx";

        if (IndexOfBytes(bytes, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], bytes.Length) >= 0)
            return "WinRarSfx";

        return "Unknown";
    }

    /// <summary>
    /// Reads the PE machine field. Returns x64, x86, ARM64, or an empty string
    /// when the file has no readable PE header.
    /// </summary>
    public static string ReadPeArchitecture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40) return "";
            if (reader.ReadUInt16() != 0x5A4D) return ""; // MZ

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > stream.Length) return "";

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return ""; // PE\0\0

            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                0xAA64 => "ARM64",
                _ => ""
            };
        }
        catch
        {
            return "";
        }
    }

    private static byte[] ReadHead(string path, int maxBytes)
    {
        using var stream = File.OpenRead(path);
        var size = (int)Math.Min(maxBytes, stream.Length);
        var buffer = new byte[size];
        stream.ReadExactly(buffer, 0, size);
        return buffer;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int limit)
    {
        var end = Math.Min(limit, haystack.Length) - needle.Length;
        for (var i = 0; i <= end; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static bool TryReadEntryNames(string path, out string[] entries)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            entries = archive.Entries.Select(e => e.FullName).ToArray();
            return true;
        }
        catch
        {
            entries = [];
            return false;
        }
    }

    private static bool IsRootEntry(string entry) => !entry.Contains('/') && !entry.Contains('\\');

    private static string LeafOf(string entry)
    {
        var index = entry.LastIndexOfAny(['/', '\\']);
        return index < 0 ? entry : entry[(index + 1)..];
    }
}
