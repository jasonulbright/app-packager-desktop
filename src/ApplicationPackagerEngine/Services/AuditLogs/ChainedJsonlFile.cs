using System.Text;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// An append-only file of one compact JSON object per line with a hash chain, shared by the audit
/// logs. An append opens the file, moves to its end and writes; nothing already in the file is ever
/// written again. The file is opened for writing with FileShare.Read, so a program that holds the file
/// open without sharing write access makes the open and the append fail alike.
/// <para>
/// Each line this product writes ends with a "Chain" property: the lowercase hex SHA-256 of the
/// hash of the line before it, a line feed, and the line's JSON text without the Chain property.
/// The hash of a line is its Chain value, or the SHA-256 of its trimmed text when it has none
/// (a line of another tool or a damaged line); the first line follows 64 zeros.
/// </para>
/// <para>
/// A read recomputes the chain from the start of what it reads: each Chain is compared with the
/// SHA-256 of the recomputed hash of the line before it and the line's own text, never with the
/// Chain value the line before it stores. The first chained line is the anchor; lines without a
/// Chain before it read as written before the chain and do not break it. From the first Chain
/// that does not match, every later line reads as not verified, with that line number, whatever
/// its own Chain holds. A line without a Chain after the anchor is not verified and its text still
/// feeds the chain, so it breaks nothing by itself. Removing lines at the end of the file is not
/// detected. The hash uses no secret: anyone who can write the file can recompute every Chain.
/// </para>
/// </summary>
internal static class ChainedJsonlFile
{
    /// <summary>The most bytes one record may take.</summary>
    public const int MaxRecordBytes = 64 * 1024;

    private const string ChainName = "Chain";
    private const string ChainPrefix = ",\"" + ChainName + "\":\"";

    /// <summary>The length of ,"Chain":"&lt;64 hex&gt;"} at the end of a chained line.</summary>
    private const int ChainSuffixLength = 76;

    /// <summary>The hash the first line of a log follows.</summary>
    public static readonly string ChainStart = new('0', 64);

    /// <summary>The most bytes read back from the end of the log to find the line an append follows.</summary>
    private const int MaxTailBytes = 1024 * 1024;

    public static bool IsChainName(string name) => string.Equals(name, ChainName, StringComparison.OrdinalIgnoreCase);

    /// <summary>One line read from the log with its chain state.</summary>
    public sealed record Line(string Text, bool Verified, bool HasChain, bool AfterChainStart, long? BrokenAt);

    /// <summary>
    /// Opens the log for appends. <paramref name="what"/> names the log in the messages, such as
    /// "The audit log". The stream is null when the log cannot be written; the message then says why.
    /// </summary>
    public static (FileStream? Stream, string Message) Open(string path, bool isDefault, string what, string exampleName)
    {
        if (!IsFullFilePath(path))
            return (null, $"{what} path must be a full path to a file, such as C:\\Logs\\{exampleName} or \\\\server\\share\\{exampleName}.");
        var folder = Path.GetDirectoryName(path)!;
        try
        {
            if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, isDefault)))
                return (null, $"{what} folder is a junction or symbolic link, so the log was not written: {folder}");
            Directory.CreateDirectory(folder);
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return (null, $"{what} file is a symbolic link or another reparse point, so it was not written: {path}");
            return (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read), "");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, $"{what} cannot be written: the file or folder is read-only or access is denied: {path}");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or DriveNotFoundException)
        {
            return (null, $"{what} folder cannot be reached: {folder}");
        }
        catch (IOException ex) when (IsSharing(ex))
        {
            return (null, $"{what} cannot be written now: another session of this product is making a change and holds it, or another program has it open: {path}. Try again when that session finishes, or close that program.");
        }
        catch (IOException)
        {
            return (null, $"{what} cannot be written: {path}. Check that the folder can be reached and has free space.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (null, $"{what} path cannot be used: {path}");
        }
    }

    private static bool IsSharing(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    public static bool IsFullFilePath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var name = Path.GetFileName(path);
            return name.Length > 0 && Path.GetDirectoryName(path) is { Length: > 0 };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Appends <paramref name="body"/>, one compact JSON object without a Chain property, with its
    /// Chain to the held stream. The caller serializes appends to one stream.
    /// </summary>
    public static AuditWrite Append(FileStream stream, string body, string path, IAppLog? log, string logArea)
    {
        if (Encoding.UTF8.GetByteCount(body) + ChainSuffixLength + 2 > MaxRecordBytes) return new AuditWrite(false, "The audit record is too large. Nothing was written.");
        try
        {
            var previous = PreviousLineHash(stream);
            var line = Encoding.UTF8.GetBytes(Chained(body, previous) + "\r\n");
            var length = stream.Seek(0, SeekOrigin.End);
            if (length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                var last = stream.ReadByte();
                stream.Seek(0, SeekOrigin.End);
                if (last != '\n') stream.Write("\r\n"u8);
            }
            stream.Write(line);
            stream.Flush(flushToDisk: true);
            return new AuditWrite(true, "The audit record was written.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Warning(logArea, $"An audit record could not be written ({ex.GetType().Name}).");
            return new AuditWrite(false, $"The audit record could not be written to {path}. Check that the folder can be reached and has free space.");
        }
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The line without white space at its ends and without a byte order mark.</summary>
    private static string Normalized(string line) => line.Trim().Trim('\uFEFF').Trim();

    /// <summary>The JSON object of a chained line without its Chain property, and the Chain value; null when the line carries no Chain.</summary>
    private static (string Body, string Chain)? SplitChain(string line)
    {
        if (line.Length <= ChainSuffixLength || !line.EndsWith("\"}", StringComparison.Ordinal)) return null;
        var start = line.Length - ChainSuffixLength;
        if (string.CompareOrdinal(line, start, ChainPrefix, 0, ChainPrefix.Length) != 0) return null;
        var chain = line.Substring(start + ChainPrefix.Length, 64);
        if (!chain.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) return null;
        return (line[..start] + "}", chain);
    }

    /// <summary>The hash the next line follows: the Chain value of a chained line, the SHA-256 of any other line.</summary>
    public static string LineHash(string line)
    {
        var normalized = Normalized(line);
        return SplitChain(normalized) is { } split ? split.Chain : Sha256(normalized);
    }

    private static string ChainOf(string body, string previousHash) => Sha256(previousHash + "\n" + body);

    private static string Chained(string body, string previousHash) => body[..^1] + ChainPrefix + ChainOf(body, previousHash) + "\"}";

    /// <summary>True when the line carries a Chain that matches its own text and the hash of the line before it.</summary>
    public static bool ChainMatches(string line, string previousHash) =>
        SplitChain(Normalized(line)) is { } split && string.Equals(ChainOf(split.Body, previousHash), split.Chain, StringComparison.Ordinal);

    /// <summary>
    /// The hash of the last line of the log, read from the held stream. A last line longer than the
    /// read window yields a hash no read reproduces, so the record that follows reads as unverified.
    /// </summary>
    private static string PreviousLineHash(FileStream stream)
    {
        var length = stream.Seek(0, SeekOrigin.End);
        if (length == 0) return ChainStart;
        var count = (int)Math.Min(length, MaxTailBytes);
        var buffer = new byte[count];
        stream.Seek(length - count, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer).TrimEnd();
        if (Normalized(text).Length == 0) return count == length ? ChainStart : Sha256(text);
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0 && count < length) return Sha256("unreadable\n" + text);
        return LineHash(text[(lastBreak + 1)..]);
    }

    /// <summary>
    /// The lines of the last <paramref name="maxBytes"/> of the file with their chain state, in file
    /// order; empty when the file does not exist. Throws IOException or UnauthorizedAccessException
    /// when the file cannot be read and <see cref="InvalidDataException"/> for a reparse point.
    /// </summary>
    public static IReadOnlyList<Line> ReadLines(string path, long maxBytes)
    {
        if (!File.Exists(path)) return [];
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("The log file is a reparse point.");
        byte[] bytes;
        var cut = false;
        long firstLine = 1;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = Math.Max(0, stream.Length - maxBytes);
            var before = start > 0 ? LineBreaksBefore(stream, start) : 0;
            stream.Seek(start, SeekOrigin.Begin);
            bytes = new byte[stream.Length - start];
            stream.ReadExactly(bytes);
            if (start > 0)
            {
                cut = true;
                var firstBreak = Array.IndexOf(bytes, (byte)'\n');
                bytes = firstBreak < 0 ? [] : bytes[(firstBreak + 1)..];
                firstLine = before + 2;
            }
        }
        return ChainStates(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n'), firstLine, cut);
    }

    /// <summary>
    /// The chain state of every line that is not empty, in file order. <paramref name="firstLine"/>
    /// is the file line number of the first entry. The line before the first line of a cut window
    /// is not read, so that line is not verified and its stored Chain is the hash the next line follows.
    /// </summary>
    private static List<Line> ChainStates(string[] physical, long firstLine, bool cut)
    {
        var states = new List<Line>(physical.Length);
        var previous = cut ? null : ChainStart;
        var anchored = false;
        long? broken = null;
        for (var k = 0; k < physical.Length; k++)
        {
            var text = Normalized(physical[k]);
            if (text.Length == 0) continue;
            var number = firstLine + k;
            var afterAnchor = anchored;
            var verified = false;
            string hash;
            if (SplitChain(text) is { } split)
            {
                if (previous is null) hash = split.Chain;
                else
                {
                    hash = ChainOf(split.Body, previous);
                    if (string.Equals(hash, split.Chain, StringComparison.Ordinal)) verified = broken is null;
                    else broken ??= number;
                }
                anchored = true;
                states.Add(new Line(text, verified, true, afterAnchor, broken));
            }
            else
            {
                hash = Sha256(text);
                states.Add(new Line(text, false, false, afterAnchor, broken));
            }
            previous = hash;
        }
        return states;
    }

    /// <summary>The number of line feeds in the first <paramref name="end"/> bytes of the stream.</summary>
    private static long LineBreaksBefore(FileStream stream, long end)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        long count = 0, read = 0;
        while (read < end)
        {
            var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - read));
            if (n <= 0) break;
            count += buffer.AsSpan(0, n).Count((byte)'\n');
            read += n;
        }
        return count;
    }
}
