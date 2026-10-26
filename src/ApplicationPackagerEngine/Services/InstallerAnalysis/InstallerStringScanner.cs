using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Finds printable ASCII and UTF-16LE strings in a file read in fixed-size chunks, so a large
/// installer is never held in memory. UTF-16 strings are found at even offsets. Each string is
/// reported once with the offset of its first occurrence and a category; strings without a
/// category fill at most half of the result so the categorised ones are not crowded out.
/// </summary>
public static class InstallerStringScanner
{
    public const int MaxStringChars = 2048;
    private const int ChunkBytes = 1024 * 1024;
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(50);

    private static readonly Regex Url = new(@"\b(?:https?|ftp)://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Registry = new(@"\b(?:HKLM|HKCU|HKCR|HKU|HKEY_[A-Z_]+|HKMU|SHCTX)\\|^(?:SOFTWARE|System\\CurrentControlSet)\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex FilePath = new(@"(?:\b[A-Za-z]:\\|%[A-Za-z_()0-9]+%\\|\\\\[A-Za-z0-9._-]+\\|\$(?:INSTDIR|PROGRAMFILES\w*|LOCALAPPDATA|APPDATA|TEMP|SYSDIR|WINDIR)\b|\{(?:app|autopf|pf|sys|tmp|commonappdata|localappdata|userappdata)\}|\[(?:ProgramFiles\w*|INSTALLDIR|TARGETDIR|SystemFolder|LocalAppDataFolder|AppDataFolder|CommonAppDataFolder)\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Command = new(@"\b(?:msiexec(?:\.exe)?|cmd(?:\.exe)?\s+/c|powershell(?:\.exe)?|pwsh(?:\.exe)?|rundll32|regsvr32|schtasks|sc(?:\.exe)?\s+(?:create|start|stop|config)|reg(?:\.exe)?\s+(?:add|delete|import)|wmic|bcdedit|netsh|icacls|takeown)\b|(?:^|\s)(?:/VERYSILENT|/SILENT|/quiet|/qn|/norestart|--silent|--unattendedmodeui)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Guid = new(@"\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Version = new(@"(?<![\d.])\d{1,5}\.\d{1,5}\.\d{1,6}(?:\.\d{1,6})?(?![\d.])", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Wordy = new(@"[A-Za-z]{4,}", RegexOptions.CultureInvariant, RegexBudget);

    /// <summary>The category of one string: Url, Registry, Path, Command, Guid, Version or Other.</summary>
    public static string Categorize(string text)
    {
        try
        {
            if (Url.IsMatch(text)) return "Url";
            if (Registry.IsMatch(text)) return "Registry";
            if (Command.IsMatch(text)) return "Command";
            if (FilePath.IsMatch(text)) return "Path";
            if (Guid.IsMatch(text)) return "Guid";
            if (Version.IsMatch(text)) return "Version";
        }
        catch (RegexMatchTimeoutException) { }
        return "Other";
    }

    /// <summary>The most strings one scan returns, whatever the requested limit.</summary>
    public const int MaxResults = 1_000_000;

    public static IReadOnlyList<InterestingString> Scan(string path, int minLength, int maxStrings, long maxBytes, CancellationToken ct) =>
        Scan(path, minLength, maxStrings, maxBytes, ct, out _);

    /// <summary>
    /// The scan, with the number of strings held for duplicate detection. Only kept strings are
    /// held, so that number never exceeds the result count.
    /// </summary>
    internal static IReadOnlyList<InterestingString> Scan(string path, int minLength, int maxStrings, long maxBytes, CancellationToken ct, out int tracked)
    {
        minLength = Math.Max(3, minLength);
        maxStrings = Math.Min(maxStrings, MaxResults);
        var results = new List<InterestingString>();
        tracked = 0;
        if (maxStrings <= 0) return results;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var otherLimit = Math.Max(1, maxStrings / 2);
        var others = 0;
        var full = false;

        void Emit(StringBuilder run, long offset, string encoding)
        {
            if (full || run.Length < minLength) return;
            var text = run.ToString();
            var key = encoding + "\0" + text;
            if (seen.Contains(key)) return;
            var category = Categorize(text);
            if (category == "Other")
            {
                if (others >= otherLimit || text.Length < Math.Max(minLength, 8) || !Wordy.IsMatch(text)) return;
                others++;
            }
            seen.Add(key);
            results.Add(new InterestingString(text, encoding, offset, category));
            if (results.Count >= maxStrings) full = true;
        }

        var ascii = new StringBuilder();
        long asciiStart = 0;
        var wide = new StringBuilder();
        long wideStart = 0;
        int pendingLow = -1;
        long pendingOffset = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
        var buffer = new byte[ChunkBytes];
        long position = 0;
        var limit = Math.Min(stream.Length, maxBytes);
        while (position < limit && !full)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - position));
            if (read <= 0) break;
            for (var i = 0; i < read && !full; i++)
            {
                var offset = position + i;
                var b = buffer[i];

                if (b >= 0x20 && b < 0x7F)
                {
                    if (ascii.Length == 0) asciiStart = offset;
                    if (ascii.Length < MaxStringChars) ascii.Append((char)b);
                }
                else
                {
                    Emit(ascii, asciiStart, "Ascii");
                    ascii.Clear();
                }

                if ((offset & 1) == 0)
                {
                    pendingLow = b;
                    pendingOffset = offset;
                }
                else if (pendingLow >= 0)
                {
                    var unit = pendingLow | (b << 8);
                    pendingLow = -1;
                    if (unit >= 0x20 && unit < 0x7F)
                    {
                        if (wide.Length == 0) wideStart = pendingOffset;
                        if (wide.Length < MaxStringChars) wide.Append((char)unit);
                    }
                    else
                    {
                        Emit(wide, wideStart, "Utf16");
                        wide.Clear();
                    }
                }
            }
            position += read;
        }
        Emit(ascii, asciiStart, "Ascii");
        Emit(wide, wideStart, "Utf16");
        tracked = seen.Count;
        return results;
    }
}
