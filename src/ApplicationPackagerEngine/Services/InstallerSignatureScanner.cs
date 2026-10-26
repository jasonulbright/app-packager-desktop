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
    /// The 32-byte marker the Velopack setup stub carries immediately after the
    /// two bundle header values. Only a setup stub embeds it; an application
    /// built with Velopack, or its Update.exe, is not itself an installer.
    /// </summary>
    private static readonly byte[] VelopackBundleSignature =
    [
        0x94, 0xf0, 0xb1, 0x7b, 0x68, 0x93, 0xe0, 0x29, 0x37, 0xeb, 0x34, 0xef, 0x53, 0xaa, 0xe7, 0xd4,
        0x2b, 0x54, 0xf5, 0x70, 0x7e, 0xf5, 0xd6, 0xf5, 0x78, 0x54, 0x98, 0x3e, 0x5e, 0x94, 0xed, 0x7d
    ];

    /// <summary>
    /// Reads the Velopack bundle header — two little-endian Int64 values, the
    /// offset and length of the embedded package — from the head of a setup
    /// stub. Returns null when the file carries no such header.
    /// </summary>
    public static VelopackBundleHeader? TryReadVelopackBundleHeader(byte[] bytes)
    {
        if (bytes.Length < 48 || bytes[0] != 0x4D || bytes[1] != 0x5A) return null;
        var index = bytes.AsSpan().IndexOf(VelopackBundleSignature);
        // The two values precede the marker; a marker in the first 16 bytes
        // cannot be one, so it is a stray copy of the constant.
        if (index < 16) return null;
        return new VelopackBundleHeader
        {
            Offset = BitConverter.ToInt64(bytes, index - 16),
            Length = BitConverter.ToInt64(bytes, index - 8)
        };
    }

    /// <summary>Reads the head of a file, up to <see cref="ScanWindowBytes"/>.</summary>
    public static byte[] ReadScanWindow(string path) => ReadHead(path, ScanWindowBytes);

    /// <summary>
    /// Returns the engine name: NativeMSI, MSP, NSIS, InnoSetup, InstallShield, WixBurn,
    /// AdvancedInstaller, BitRock, Squirrel, Velopack, SevenZipSfx, WinRarSfx, MSIX,
    /// MsixBundle, Chocolatey, NuGet, Intunewin, PsadtV3, PsadtV4, or Unknown.
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

        // Velopack before Squirrel: a Velopack setup carries Squirrel
        // compatibility strings in its payload, and its own bundle header is
        // the stronger fact.
        if (TryReadVelopackBundleHeader(bytes) is not null) return "Velopack";

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

    /// <summary>The most entries a zip may have for its layout to be read; a larger zip is identified by its signature.</summary>
    public const int MaxLayoutEntries = 100_000;

    private static bool TryReadEntryNames(string path, out string[] entries)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            ZipCentralDirectory.Check(stream, MaxLayoutEntries, "read");
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
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

/// <summary>Position and size of the package a Velopack setup stub embeds.</summary>
public sealed class VelopackBundleHeader
{
    public long Offset { get; init; }
    public long Length { get; init; }
}
