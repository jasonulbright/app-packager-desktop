using System.Buffers.Binary;
using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>The entry count and byte size of a zip's central directory, counted from the file's own records.</summary>
internal sealed record ZipDirectoryInfo(long Entries, long Bytes);

/// <summary>
/// Checks a zip's central directory before a <see cref="System.IO.Compression.ZipArchive"/> reads it.
/// ZipArchive loads every central record that follows the directory start, whatever count the end
/// record declares, so the check walks those same records (fixed 46-byte headers and their name,
/// extra and comment lengths) without keeping them, and refuses the archive past the limits.
/// </summary>
internal static class ZipCentralDirectory
{
    /// <summary>The largest central directory accepted; it bounds the names and entry objects held in memory.</summary>
    public const long MaxDirectoryBytes = 128L * 1024 * 1024;

    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralSignature = 0x02014b50;
    private const int EndSize = 22;
    private const int CentralHeaderSize = 46;
    private const int TailBytes = EndSize + ushort.MaxValue;

    /// <summary>
    /// The verified count and size, or null when the stream has no end-of-central-directory record
    /// (not a zip; ZipArchive then fails without reading entries). Throws <see cref="InstallerAnalysisException"/>
    /// when the archive has more than <paramref name="maxEntries"/> entries or a directory over
    /// <see cref="MaxDirectoryBytes"/>; the message ends with "it was not <paramref name="verb"/>.".
    /// The stream position is restored.
    /// </summary>
    public static ZipDirectoryInfo? Check(Stream stream, int maxEntries, string verb)
    {
        var restore = stream.Position;
        try
        {
            return CheckCore(stream, maxEntries, verb);
        }
        finally
        {
            stream.Position = restore;
        }
    }

    /// <summary>Opens the file and runs <see cref="Check(Stream, int, string)"/> on it.</summary>
    public static ZipDirectoryInfo? Check(string path, int maxEntries, string verb)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return Check(stream, maxEntries, verb);
    }

    private static InstallerAnalysisException TooMany(int maxEntries, string verb) =>
        new(string.Format(CultureInfo.InvariantCulture, "The archive has more than {0:N0} entries; it was not {1}.", maxEntries, verb));

    private static InstallerAnalysisException TooLarge(string verb) =>
        new($"The archive's central directory is larger than the {MaxDirectoryBytes / (1024 * 1024)} MB limit; it was not {verb}.");

    private static ZipDirectoryInfo? CheckCore(Stream stream, int maxEntries, string verb)
    {
        var length = stream.Length;
        if (length < EndSize) return null;
        var take = (int)Math.Min(length, TailBytes);
        var tail = new byte[take];
        stream.Position = length - take;
        stream.ReadExactly(tail, 0, take);

        for (var i = take - EndSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndSignature) continue;
            long endOffset = length - take + i;
            long declared = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
            long start = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            if (declared == 0xFFFF || size == 0xFFFFFFFF || start == 0xFFFFFFFF)
            {
                if (ReadZip64(stream, endOffset) is { } zip64) (declared, size, start) = zip64;
            }
            if (declared > maxEntries) throw TooMany(maxEntries, verb);
            if (size > MaxDirectoryBytes) throw TooLarge(verb);
            if (start < 0 || start > length) return new ZipDirectoryInfo(declared, size);
            var (walked, bytes) = Walk(stream, start, maxEntries, verb);
            return new ZipDirectoryInfo(Math.Max(walked, declared), Math.Max(bytes, size));
        }
        return null;
    }

    private static (long Entries, long Size, long Start)? ReadZip64(Stream stream, long endOffset)
    {
        if (endOffset < 20) return null;
        var locator = new byte[20];
        stream.Position = endOffset - 20;
        stream.ReadExactly(locator, 0, 20);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature) return null;
        var recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        if (recordOffset > (ulong)(stream.Length - 56)) return null;
        var record = new byte[56];
        stream.Position = (long)recordOffset;
        stream.ReadExactly(record, 0, 56);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndSignature) return null;
        static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
        return (Clamp(BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32))),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40))),
            Clamp(BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48))));
    }

    /// <summary>Counts the consecutive central records from <paramref name="start"/>; nothing but the count and size is kept.</summary>
    private static (long Entries, long Bytes) Walk(Stream stream, long start, int maxEntries, string verb)
    {
        var header = new byte[CentralHeaderSize];
        long entries = 0;
        var position = start;
        var length = stream.Length;
        while (position + CentralHeaderSize <= length)
        {
            stream.Position = position;
            stream.ReadExactly(header, 0, CentralHeaderSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralSignature) break;
            var variable = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))
                + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30))
                + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            entries++;
            if (entries > maxEntries) throw TooMany(maxEntries, verb);
            position += CentralHeaderSize + variable;
            if (position - start > MaxDirectoryBytes) throw TooLarge(verb);
        }
        return (entries, position - start);
    }
}
