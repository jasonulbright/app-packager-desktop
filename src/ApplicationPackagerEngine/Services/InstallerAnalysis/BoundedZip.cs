using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads zip-based packages (MSIX, APPX, bundles, nupkg, intunewin, zip, the package inside a
/// Velopack setup) with every read bounded: entry count, expanded size, and the ratio of
/// expanded to compressed bytes. Declared sizes in the central directory are not trusted;
/// the bytes actually produced are counted.
/// </summary>
internal sealed class BoundedZip : IDisposable
{
    private const int UnixSymlink = 0xA000;

    private readonly FileStream _file;
    private readonly MemoryMappedFile? _mapping;
    private readonly Stream _view;
    public ZipArchive Archive { get; }

    private BoundedZip(FileStream file, MemoryMappedFile? mapping, Stream view, ZipArchive archive)
    {
        _file = file;
        _mapping = mapping;
        _view = view;
        Archive = archive;
    }

    /// <summary>Opens the zip at the start of the file, or the package a Velopack setup stub embeds.</summary>
    public static BoundedZip Open(string path, bool velopack = false)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (!velopack)
                return new BoundedZip(file, null, file, new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true));

            var head = new byte[(int)Math.Min(InstallerSignatureScanner.ScanWindowBytes, file.Length)];
            file.ReadExactly(head, 0, head.Length);
            var header = InstallerSignatureScanner.TryReadVelopackBundleHeader(head)
                ?? throw new InvalidDataException("The Velopack package is missing.");
            if (header.Offset <= 0 || header.Length <= 0 || header.Offset > file.Length || header.Length > file.Length - header.Offset)
                throw new InvalidDataException("The Velopack package offset or length is outside the file.");
            var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            var view = mapping.CreateViewStream(header.Offset, header.Length, MemoryMappedFileAccess.Read);
            return new BoundedZip(file, mapping, view, new ZipArchive(view, ZipArchiveMode.Read, leaveOpen: true));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>The central directory entries, at most <paramref name="maxEntries"/>; throws past the limit.</summary>
    public IReadOnlyList<ZipArchiveEntry> Entries(int maxEntries)
    {
        if (DeclaredEntryCount(_view) is { } declared && declared > maxEntries)
            throw new InstallerAnalysisException($"The archive has more than {maxEntries:N0} entries; it was not listed.");
        var entries = Archive.Entries;
        if (entries.Count > maxEntries)
            throw new InstallerAnalysisException($"The archive has more than {maxEntries:N0} entries; it was not listed.");
        return entries;
    }

    /// <summary>
    /// The entry count the end-of-central-directory record declares (the zip64 record when
    /// the classic field is saturated), read before the archive builds its entry list.
    /// </summary>
    internal static long? DeclaredEntryCount(Stream stream)
    {
        const int Tail = 65557;
        var length = stream.Length;
        var take = (int)Math.Min(length, Tail);
        if (take < 22) return null;
        var buffer = new byte[take];
        stream.Position = length - take;
        stream.ReadExactly(buffer, 0, take);
        for (var i = take - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(buffer, i) != 0x06054b50) continue;
            long count = BitConverter.ToUInt16(buffer, i + 10);
            if (count == 0xFFFF && i >= 20 && BitConverter.ToUInt32(buffer, i - 20) == 0x07064b50)
            {
                var zip64 = (long)BitConverter.ToUInt64(buffer, i - 12);
                if (zip64 >= 0 && zip64 + 56 <= length)
                {
                    var record = new byte[56];
                    stream.Position = zip64;
                    stream.ReadExactly(record, 0, 56);
                    if (BitConverter.ToUInt32(record, 0) == 0x06064b50)
                    {
                        var total = BitConverter.ToUInt64(record, 32);
                        count = total > long.MaxValue ? long.MaxValue : (long)total;
                    }
                }
            }
            stream.Position = 0;
            return count;
        }
        stream.Position = 0;
        return null;
    }

    public static bool IsSymbolicLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == UnixSymlink;

    public static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>The entry text decoded as UTF-8 (with byte order mark detection), or null when absent or larger than the limit.</summary>
    public string? ReadText(string name, int maxBytes)
    {
        var entry = Archive.GetEntry(name)
            ?? Archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var buffer = new MemoryStream();
        Copy(entry, buffer, maxBytes, 1000, CancellationToken.None);
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Copies an entry's expanded bytes. Stops with <see cref="InstallerAnalysisException"/> when the
    /// output passes <paramref name="maxBytes"/> or the compression ratio limit.
    /// </summary>
    public static long Copy(ZipArchiveEntry entry, Stream target, long maxBytes, int maxRatio, CancellationToken ct)
    {
        if (IsSymbolicLink(entry)) throw new InstallerAnalysisException("The entry is a symbolic link; it was not extracted.");
        var ratioLimit = Math.Max(1L, entry.CompressedLength) * Math.Max(1, maxRatio) + 1024 * 1024;
        var buffer = new byte[81920];
        long total = 0;
        using var source = entry.Open();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) return total;
            total += read;
            if (total > maxBytes) throw new InstallerAnalysisException("The entry is larger than the extraction limit; it was not extracted.");
            if (total > ratioLimit) throw new InstallerAnalysisException("The entry expands far beyond its compressed size; it was not extracted.");
            target.Write(buffer, 0, read);
        }
    }

    public void Dispose()
    {
        Archive.Dispose();
        if (!ReferenceEquals(_view, _file)) _view.Dispose();
        _mapping?.Dispose();
        _file.Dispose();
    }
}
