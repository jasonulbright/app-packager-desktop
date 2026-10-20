using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>What the compiled NSIS script says about the install.</summary>
public sealed class NsisMetadata
{
    public bool HeaderAvailable { get; set; }
    /// <summary>lzma, zlib, bzip2 or none. bzip2 headers are not decoded.</summary>
    public string Compression { get; set; } = "";
    public bool Solid { get; set; }
    public bool Unicode { get; set; }
    /// <summary>The installer Name from the first language table.</summary>
    public string Name { get; set; } = "";
    /// <summary>Literal DisplayName / DisplayVersion / Publisher written to the ARP key; empty when computed at run time.</summary>
    public string DisplayName { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string Publisher { get; set; } = "";
    /// <summary>Compile-time or resolved install folder in NSIS notation.</summary>
    public string InstallDir { get; set; } = "";
    public string InstallDirWindows { get; set; } = "";
    public string InstallDirRegKey { get; set; } = "";
    public string UninstallerPath { get; set; } = "";
    public string UninstallerPathWindows { get; set; } = "";
    public string SilentUninstallCommand { get; set; } = "";
    public string UninstallRegistryKey { get; set; } = "";
    public string UninstallRegistryKeyNote { get; set; } = "";
    public string RegistryHive { get; set; } = "";
    public string RegistryView { get; set; } = "";
    /// <summary>Every value the script writes under the chosen ARP key, in NSIS notation.</summary>
    public Dictionary<string, string> ArpValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> InstallDirCandidates { get; set; } = [];
    public string RequestedExecutionLevel { get; set; } = "";
    /// <summary>"all" or "current": the shell folder context the install folder resolves in.</summary>
    public string ShellVarContext { get; set; } = "current";
    public string InstallContext { get; set; } = "";
    /// <summary>The mode the installer takes without a switch: AllUsers or CurrentUser.</summary>
    public string InstallMode { get; set; } = "";
    /// <summary>Modes the script accepts, in the fixed order CurrentUser, AllUsers.</summary>
    public List<string> InstallModes { get; set; } = [];
    public string AllUsersSwitch { get; set; } = "";
    public string CurrentUserSwitch { get; set; } = "";
    public Dictionary<string, InstallBranch> ModeVariants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Note { get; set; } = "";
}

/// <summary>
/// Reads an NSIS installer's compiled header: install directory, uninstaller,
/// Add/Remove Programs registration and install modes, from the entry list
/// and string table rather than from installing it.
/// </summary>
public static class NsisScriptReader
{
    private const int FirstHeaderSize = 28;

    /// <summary>
    /// The largest file read whole when the leading window misses the header. A header sits
    /// right after the stub, so a larger file is reported as not found instead of being loaded.
    /// </summary>
    public const long WholeFileLimitBytes = 512L * 1024 * 1024;

    // CSIDL pairs (current user, all users) as makensis encodes the built-in
    // shell constants; the ProgramFiles family uses a registry lookup instead.
    private static readonly Dictionary<string, string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1A,23"] = "APPDATA", ["1C,23"] = "LOCALAPPDATA", ["28,28"] = "PROFILE",
        ["05,2E"] = "DOCUMENTS", ["10,19"] = "DESKTOP", ["02,17"] = "SMPROGRAMS",
        ["0B,16"] = "STARTMENU", ["07,18"] = "SMSTARTUP", ["24,24"] = "WINDIR",
        ["25,25"] = "SYSDIR", ["15,2D"] = "TEMPLATES", ["06,1F"] = "FAVORITES",
        ["0D,35"] = "MUSIC", ["27,36"] = "PICTURES", ["0E,37"] = "VIDEOS",
        ["14,14"] = "FONTS", ["1B,1B"] = "PRINTHOOD", ["13,13"] = "NETHOOD",
        ["08,08"] = "RECENT", ["09,09"] = "SENDTO", ["1A,1A"] = "QUICKLAUNCH",
        ["38,38"] = "RESOURCES", ["39,39"] = "RESOURCES_LOCALIZED", ["3B,3B"] = "CDBURN_AREA",
        ["30,2F"] = "ADMINTOOLS", ["22,22"] = "HISTORY", ["21,21"] = "COOKIES",
        ["20,20"] = "INTERNET_CACHE"
    };

    private static readonly string[] VarNames =
        ["CMDLINE", "INSTDIR", "OUTDIR", "EXEDIR", "LANGUAGE", "TEMP", "PLUGINSDIR", "EXEPATH", "EXEFILE", "HWNDPARENT", "_CLICK", "_OUTDIR"];

    private static readonly Regex Unresolvable = new(@"\$(\d|R\d|__VAR|\(LangString|OUTDIR|EXEDIR|PLUGINSDIR|TEMP|CMDLINE|EXEPATH|EXEFILE|LANGUAGE)", RegexOptions.Compiled);
    private static readonly Regex MachineFolders = new(@"^\$(PROGRAMFILES|COMMONFILES)", RegexOptions.Compiled);
    private static readonly Regex UserFolders = new(@"^\$(LOCALAPPDATA|APPDATA|PROFILE)", RegexOptions.Compiled);
    private static readonly Regex UserFoldersWide = new(@"^\$(LOCALAPPDATA|APPDATA|PROFILE|DOCUMENTS|DESKTOP)", RegexOptions.Compiled);
    private static readonly Regex DriveOrShare = new(@"^[A-Za-z]:\\", RegexOptions.Compiled);

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>
    /// File offset of the firstheader, or -1. It is flags (4) + 0xDEADBEEF (4)
    /// + "NullsoftInst" (12) + header length (4) + total length (4) at a
    /// 512-byte boundary after the PE image; the exehead itself contains the
    /// marker text, so only an aligned hit whose lengths fit the file counts.
    /// </summary>
    public static int FindFirstHeader(byte[] bytes, long fileLength = 0)
    {
        if (bytes.Length < FirstHeaderSize + 4) return -1;
        var text = Latin1.GetString(bytes);
        // 0xDEADBEEF little-endian as Latin-1 characters, then the marker text.
        var marker = new string([(char)0xEF, (char)0xBE, (char)0xAD, (char)0xDE]) + "NullsoftInst";
        var available = fileLength > 0 ? fileLength : bytes.Length;
        var from = 0;
        while (from <= bytes.Length - 16)
        {
            var i = text.IndexOf(marker, from, StringComparison.Ordinal);
            if (i < 0) break;
            from = i + 1;
            if (i < 4) continue;
            var start = i - 4;
            if (start % 512 != 0) continue;
            if (start + FirstHeaderSize > bytes.Length) continue;
            var headerLength = BitConverter.ToUInt32(bytes, start + 20);
            var totalLength = BitConverter.ToUInt32(bytes, start + 24);
            if (headerLength < 300 || totalLength < FirstHeaderSize) continue;
            if (totalLength > (available - start) + 4) continue;
            return start;
        }
        return -1;
    }

    private sealed class Expanded
    {
        public byte[]? Header;
        public string Compression = "";
        public bool Solid;
        public bool Filtered;
        public bool TooLarge;
        public string Note = "";
    }

    /// <summary>The largest decompressed header block the reader accepts.</summary>
    public const int MaxHeaderBytes = 64 * 1024 * 1024;

    public const string HeaderTooLargeNote = "The NSIS header length field is larger than the 64 MB limit; the header was not decoded.";

    /// <summary>
    /// Decompresses the header block that follows the firstheader. Solid
    /// builds carry one stream whose first header-length bytes (behind a
    /// 4-byte length) are the header; non-solid builds prefix each block with
    /// its length, high bit set when compressed. LZMA streams start with the
    /// properties byte and a 4-byte dictionary size, optionally preceded by a
    /// 0/1 BCJ filter flag. NSIS bzip2 is a stripped stream and is not decoded.
    /// </summary>
    private static Expanded ExpandHeader(byte[] bytes, int firstHeader)
    {
        var result = new Expanded();
        var rawHeaderLength = BitConverter.ToUInt32(bytes, firstHeader + 20);
        // The field sizes the decode buffer; it is checked before anything is allocated from it.
        if (rawHeaderLength > MaxHeaderBytes)
        {
            result.Note = HeaderTooLargeNote;
            result.TooLarge = true;
            return result;
        }
        var headerLength = (int)rawHeaderLength;
        var dataStart = firstHeader + FirstHeaderSize;
        if (dataStart + 8 > bytes.Length) { result.Note = "Installer data block is truncated."; return result; }

        bool IsLzmaAt(int p) =>
            p + 6 < bytes.Length && bytes[p] == 0x5D && bytes[p + 1] == 0 && bytes[p + 2] == 0 && bytes[p + 5] == 0;
        bool IsBzip2At(int p) => p + 4 < bytes.Length && bytes[p] == 0x31 && bytes[p + 1] < 14;
        int PrefixLength(bool solid) => solid ? 4 : 0;

        byte[]? TakeHeader(byte[]? output, bool solid)
        {
            if (output is null) return null;
            if (output.Length != headerLength + PrefixLength(solid)) return null;
            if (solid)
            {
                var declared = BitConverter.ToUInt32(output, 0) & 0x7FFFFFFF;
                if (declared != headerLength) return null;
                return output[4..];
            }
            return output;
        }

        byte[]? LzmaBlock(int p, int end, bool filtered, bool solid)
        {
            var props = bytes[p];
            var dict = BitConverter.ToUInt32(bytes, p + 1);
            var want = headerLength + PrefixLength(solid);
            var output = LzmaDecoder.Decode(bytes, p + 5, end - (p + 5), props, dict, want, out _);
            if (filtered && output.Length > 0) LzmaDecoder.DecodeBcjX86(output, output.Length);
            return TakeHeader(output, solid);
        }

        byte[]? DeflateBlock(int p, int end, bool solid)
        {
            try
            {
                using var ms = new MemoryStream(bytes, p, end - p, false);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                var want = headerLength + PrefixLength(solid);
                var output = new byte[want];
                var read = 0;
                while (read < want)
                {
                    var n = ds.Read(output, read, want - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != want) return null;
                return TakeHeader(output, solid);
            }
            catch
            {
                return null;
            }
        }

        Expanded Bzip2(bool solid)
        {
            result.Compression = "bzip2";
            result.Solid = solid;
            result.Note = "NSIS bzip2 streams are not decoded; header fields are unavailable.";
            return result;
        }

        var end = bytes.Length;
        var first = BitConverter.ToUInt32(bytes, dataStart);

        if (IsLzmaAt(dataStart))
        {
            result.Compression = "lzma"; result.Solid = true;
            result.Header = LzmaBlock(dataStart, end, false, true);
            if (result.Header is null) result.Note = "LZMA header stream did not decode.";
            return result;
        }
        if ((bytes[dataStart] == 0 || bytes[dataStart] == 1) && IsLzmaAt(dataStart + 1))
        {
            result.Compression = "lzma"; result.Solid = true; result.Filtered = bytes[dataStart] == 1;
            result.Header = LzmaBlock(dataStart + 1, end, result.Filtered, true);
            if (result.Header is null) result.Note = "LZMA header stream did not decode.";
            return result;
        }
        // Solid bzip2 starts with the block-size byte 0x31; a non-solid length
        // prefix under 16 MB carries 0x80 in its top byte instead.
        if (IsBzip2At(dataStart) && bytes[dataStart + 3] != 0x80) return Bzip2(true);

        if ((first & 0x80000000) != 0)
        {
            var blockLength = (int)(first & 0x7FFFFFFF);
            var blockStart = dataStart + 4;
            var blockEnd = Math.Min(blockStart + blockLength, end);
            if (IsLzmaAt(blockStart))
            {
                result.Compression = "lzma";
                result.Header = LzmaBlock(blockStart, blockEnd, false, false);
                if (result.Header is null) result.Note = "LZMA header block did not decode.";
                return result;
            }
            if ((bytes[blockStart] == 0 || bytes[blockStart] == 1) && IsLzmaAt(blockStart + 1))
            {
                result.Compression = "lzma"; result.Filtered = bytes[blockStart] == 1;
                result.Header = LzmaBlock(blockStart + 1, blockEnd, result.Filtered, false);
                if (result.Header is null) result.Note = "LZMA header block did not decode.";
                return result;
            }
            if (IsBzip2At(blockStart)) return Bzip2(false);
            result.Compression = "zlib";
            result.Header = DeflateBlock(blockStart, blockEnd, false);
            if (result.Header is null) result.Note = "Deflate header block did not decode.";
            return result;
        }
        if (first == headerLength && dataStart + 4 + headerLength <= end)
        {
            result.Compression = "none";
            result.Header = bytes[(dataStart + 4)..(dataStart + 4 + headerLength)];
            return result;
        }

        result.Compression = "zlib"; result.Solid = true;
        result.Header = DeflateBlock(dataStart, end, true);
        if (result.Header is null) result.Note = "Deflate header stream did not decode.";
        return result;
    }

    /// <summary>The decoded header block with its string table and language strings.</summary>
    public sealed class HeaderTable
    {
        public byte[]? Header;
        public string Table = "";
        public bool Unicode;
        public Dictionary<int, string> LangStrings = new();
        public string Name = "";
        public int EntriesOffset;
        public int EntriesCount;
        public int SectionsOffset;
        public int SectionsCount;
        public int SectionSize;
        public string Compression = "";
        public bool Solid;
        public bool FirstHeaderFound;
        public string Note = "";

        public string Str(int ptr) => ReadString(Table, ptr, Unicode, LangStrings);
    }

    /// <summary>
    /// Loads the decompressed header block and its string table. The header
    /// sits at the front of the appended data, so a 16 MB window normally
    /// covers it; the whole file is read only when the window misses the
    /// firstheader or truncates the compressed header.
    /// </summary>
    public static HeaderTable ReadHeaderTable(string path, long maxWholeFileBytes = WholeFileLimitBytes)
    {
        var result = new HeaderTable();

        static byte[] ReadWindow(string p, long length)
        {
            using var fs = File.OpenRead(p);
            var take = (int)Math.Min(Math.Min(fs.Length, length), Array.MaxLength);
            var buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buffer, read, take - read);
                if (n <= 0) break;
                read += n;
            }
            return buffer;
        }

        var fileLength = new FileInfo(path).Length;
        var bytes = ReadWindow(path, 16L * 1024 * 1024);
        var fh = FindFirstHeader(bytes, fileLength);
        Expanded? expanded = null;
        if (fh >= 0) expanded = ExpandHeader(bytes, fh);
        if ((fh < 0 || expanded is { Header: null, TooLarge: false }) && fileLength > bytes.Length && fileLength <= maxWholeFileBytes)
        {
            bytes = ReadWindow(path, fileLength);
            fh = FindFirstHeader(bytes, fileLength);
            if (fh >= 0) expanded = ExpandHeader(bytes, fh);
        }
        if (fh < 0 || expanded is null) { result.Note = "NSIS firstheader not found."; return result; }
        result.FirstHeaderFound = true;
        result.Compression = expanded.Compression;
        result.Solid = expanded.Solid;
        if (expanded.Header is null) { result.Note = expanded.Note; return result; }
        var h = expanded.Header;
        if (h.Length < 300) { result.Note = "Header block shorter than the fixed layout."; return result; }

        int BlockOffset(int i) => BitConverter.ToInt32(h, 4 + (i * 8));
        int BlockCount(int i) => BitConverter.ToInt32(h, 8 + (i * 8));
        var sectionsOffset = BlockOffset(1);
        var sectionsCount = BlockCount(1);
        var entriesOffset = BlockOffset(2);
        var entriesCount = BlockCount(2);
        var stringsOffset = BlockOffset(3);
        var langOffset = BlockOffset(4);
        var langCount = BlockCount(4);
        var stringsEnd = langOffset;
        if (stringsOffset <= 0 || stringsEnd <= stringsOffset || stringsEnd > h.Length ||
            entriesOffset < 0 || entriesOffset + ((long)entriesCount * 28) > h.Length)
        {
            result.Note = "Header block table is inconsistent.";
            return result;
        }
        var unicode = BitConverter.ToUInt16(h, stringsOffset) == 0;
        result.Header = h;
        result.Unicode = unicode;
        result.EntriesOffset = entriesOffset;
        result.EntriesCount = entriesCount;
        if (sectionsCount > 0 && sectionsOffset > 0 && entriesOffset > sectionsOffset)
        {
            result.SectionsOffset = sectionsOffset;
            result.SectionsCount = sectionsCount;
            result.SectionSize = (entriesOffset - sectionsOffset) / sectionsCount;
        }

        var table = unicode
            ? Encoding.Unicode.GetString(h, stringsOffset, stringsEnd - stringsOffset)
            : Latin1.GetString(h, stringsOffset, stringsEnd - stringsOffset);
        result.Table = table;

        // Language table: LANGID(2) + dlg_offset(4) + rtl(4) in NSIS 3, LANGID(2)
        // + dlg_offset(4) in NSIS 2; the first layout whose Name slot (index 2)
        // points inside the strings block and decodes to text wins.
        var langStrings = new Dictionary<int, string>();
        var langTableSize = BitConverter.ToInt32(h, 100);
        if (langCount > 0 && langOffset > 0 && langTableSize > 10 && langOffset + langTableSize <= h.Length)
        {
            foreach (var prefix in new[] { 10, 6, 8 })
            {
                var count = (langTableSize - prefix) / 4;
                if (count < 3) continue;
                var namePtr = BitConverter.ToInt32(h, langOffset + prefix + (2 * 4));
                if (namePtr <= 0 || namePtr >= table.Length) continue;
                var candidate = ReadString(table, namePtr, unicode, null);
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                for (var i = 0; i < count; i++)
                {
                    var ptr = BitConverter.ToInt32(h, langOffset + prefix + (i * 4));
                    if (ptr > 0 && ptr < table.Length) langStrings[i] = ReadString(table, ptr, unicode, null);
                }
                result.Name = candidate;
                break;
            }
        }
        result.LangStrings = langStrings;
        return result;
    }

    /// <summary>
    /// Decodes one string-table entry into script notation. Escape codes 1
    /// (LangString), 2 (shell folder), 3 (variable) and 4 (literal next unit)
    /// precede one code unit in Unicode builds and two bytes in ANSI builds.
    /// </summary>
    public static string ReadString(string table, int ptr, bool unicode, Dictionary<int, string>? langStrings, int depth = 0)
    {
        if (ptr < 0 || ptr >= table.Length) return "";
        var end = table.IndexOf('\0', ptr);
        if (end < 0) end = table.Length;
        if (end == ptr) return "";
        var sb = new StringBuilder();
        var pos = ptr;
        while (pos < end)
        {
            int u = table[pos];
            pos++;
            var code = 0;
            if (u >= 1 && u <= 4) code = u;
            else if (unicode && u >= 0xE000 && u <= 0xE003) code = u - 0xE000 + 1;
            if (code == 0)
            {
                sb.Append((char)u);
                continue;
            }
            int lo, hi, arg;
            if (unicode)
            {
                if (pos >= end) break;
                arg = table[pos];
                pos++;
                lo = arg & 0xFF;
                hi = (arg >> 8) & 0xFF;
            }
            else
            {
                if (pos + 1 >= end) break;
                lo = table[pos];
                hi = table[pos + 1];
                arg = lo;
                pos += 2;
            }
            switch (code)
            {
                case 4:
                    sb.Append((char)arg);
                    break;
                case 3:
                {
                    var index = (lo & 0x7F) | ((hi & 0x7F) << 7);
                    if (index < 10) sb.Append('$').Append(index);
                    else if (index < 20) sb.Append("$R").Append(index - 10);
                    else if (index - 20 < VarNames.Length) sb.Append('$').Append(VarNames[index - 20]);
                    else sb.Append("$__VAR").Append(index);
                    break;
                }
                case 2:
                {
                    // Registry-resolved folder: NSIS 3 sets bit 7 of the low byte, bit 6
                    // selects the 64-bit view and the low bits point at the value name
                    // (ProgramFilesDir / CommonFilesDir); the 2.x line sets bit 7 of the
                    // high byte and only ever resolved Program Files this way.
                    if ((lo & 0x80) != 0)
                    {
                        var valueName = depth < 1 ? ReadString(table, lo & 0x3F, unicode, null, depth + 1) : "";
                        var is64 = (lo & 0x40) != 0;
                        var name = valueName.StartsWith("ProgramFilesDir", StringComparison.Ordinal) ? (is64 ? "PROGRAMFILES64" : "PROGRAMFILES")
                            : valueName.StartsWith("CommonFilesDir", StringComparison.Ordinal) ? (is64 ? "COMMONFILES64" : "COMMONFILES")
                            : "SHELL[" + valueName + "]";
                        sb.Append('$').Append(name);
                    }
                    else if ((hi & 0x80) != 0)
                    {
                        sb.Append((lo & 0x40) != 0 ? "$PROGRAMFILES64" : "$PROGRAMFILES");
                    }
                    else
                    {
                        var key = $"{lo:X2},{hi:X2}";
                        sb.Append('$').Append(ShellNames.TryGetValue(key, out var shell) ? shell : "SHELL[" + key + "]");
                    }
                    break;
                }
                case 1:
                {
                    var index = (lo & 0x7F) | ((hi & 0x7F) << 7);
                    if (langStrings is not null && langStrings.TryGetValue(index, out var resolved)) sb.Append(resolved);
                    else sb.Append("$(LangString#").Append(index).Append(')');
                    break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Rewrites NSIS folder constants in a path to Windows environment form.
    /// $INSTDIR is replaced with the supplied install directory first;
    /// unresolvable variables ($0, $EXEDIR, LangStrings) stay in place.
    /// </summary>
    public static string ToWindowsPath(string path, string installDir = "", bool allUsersContext = false)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path;
        if (!string.IsNullOrEmpty(installDir)) p = p.Replace("$INSTDIR", installDir);
        var map = new (string Key, string Value)[]
        {
            ("$PROGRAMFILES64", "%ProgramW6432%"),
            ("$PROGRAMFILES32", "%ProgramFiles(x86)%"),
            ("$PROGRAMFILES", "%ProgramFiles(x86)%"),
            ("$COMMONFILES64", "%CommonProgramW6432%"),
            ("$COMMONFILES32", "%CommonProgramFiles(x86)%"),
            ("$COMMONFILES", "%CommonProgramFiles(x86)%"),
            ("$LOCALAPPDATA", allUsersContext ? "%ProgramData%" : "%LOCALAPPDATA%"),
            ("$APPDATA", allUsersContext ? "%ProgramData%" : "%APPDATA%"),
            ("$PROFILE", "%USERPROFILE%"),
            ("$DOCUMENTS", allUsersContext ? @"%PUBLIC%\Documents" : @"%USERPROFILE%\Documents"),
            ("$DESKTOP", allUsersContext ? @"%PUBLIC%\Desktop" : @"%USERPROFILE%\Desktop"),
            ("$SMPROGRAMS", allUsersContext ? @"%ProgramData%\Microsoft\Windows\Start Menu\Programs" : @"%APPDATA%\Microsoft\Windows\Start Menu\Programs"),
            ("$STARTMENU", allUsersContext ? @"%ProgramData%\Microsoft\Windows\Start Menu" : @"%APPDATA%\Microsoft\Windows\Start Menu"),
            ("$WINDIR", "%SystemRoot%"),
            ("$SYSDIR", @"%SystemRoot%\System32"),
            ("$TEMP", "%TEMP%")
        };
        foreach (var (key, value) in map) p = p.Replace(key, value);
        return p;
    }

    private static string RootKeyName(uint root) => root switch
    {
        0x80000000 => "HKCR",
        0x80000001 => "HKCU",
        0x80000002 => "HKLM",
        0x80000003 => "HKU",
        0 => "SHCTX",
        _ => $"HKEY(0x{root:X8})"
    };

    /// <summary>
    /// Splits a root-key parameter into the hive name and the forced registry
    /// view carried by HKLM32 / HKLM64 / SHCTX32 style roots.
    /// </summary>
    public static (string Name, string View) ResolveRegistryRoot(int root)
    {
        var u = unchecked((uint)root);
        var view = (u & 0x60000000) switch
        {
            0x40000000 => "32",
            0x20000000 => "64",
            0x60000000 => "any",
            _ => ""
        };
        return (RootKeyName(u & 0x9FFFFFFF), view);
    }

    /// <summary>
    /// Decompresses the header block and walks the entry list for
    /// WriteUninstaller, the ARP WriteRegStr calls and the SetShellVarContext
    /// / SetRegView flags in script order. Fields the script computes at run
    /// time stay in NSIS notation.
    /// </summary>
    public static NsisMetadata Read(string path)
    {
        var meta = new NsisMetadata();
        if (!File.Exists(path)) { meta.Note = "Installer not found: " + path; return meta; }
        meta.RequestedExecutionLevel = PeResources.ReadRequestedExecutionLevel(path);

        var ctx = ReadHeaderTable(path);
        meta.Compression = ctx.Compression;
        meta.Solid = ctx.Solid;
        if (ctx.Header is null) { meta.Note = ctx.Note; return meta; }
        var h = ctx.Header;
        meta.HeaderAvailable = true;
        meta.Unicode = ctx.Unicode;
        meta.Name = ctx.Name;

        var installDirPtr = BitConverter.ToInt32(h, 280);
        if (installDirPtr > 0) meta.InstallDir = ctx.Str(installDirPtr);
        var regRoot = BitConverter.ToUInt32(h, 68);
        var regKeyPtr = BitConverter.ToInt32(h, 72);
        var regValPtr = BitConverter.ToInt32(h, 76);
        if (regKeyPtr > 0)
            meta.InstallDirRegKey = $"{RootKeyName(regRoot)}\\{ctx.Str(regKeyPtr)}\\{ctx.Str(regValPtr)}";

        // Entry walk in script order. Opcodes: 13 SetFlag (1 = SetShellVarContext,
        // 12 = SetRegView), 25 StrCpy, 51 WriteReg, 62 WriteUninstaller. Code
        // order is not execution order (.onInit sits wherever the script put
        // it), so flags are read as "seen anywhere" plus "last value before the
        // ARP write" and resolved against the chosen folder afterwards.
        var allUsersSeen = false;
        var regView = new NsisRegView();
        var arpCandidates = new List<(string Subkey, string Root, string ForcedView, bool SeqView64, Dictionary<string, string> Values)>();
        var uninstallerPath = "";
        var instDirAssignments = new List<string>();
        var userVars = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastInstDirResolvable = false;

        // $N / $RN at the start of a value is replaced with the last literal
        // assigned to that user variable during the walk.
        string ResolveUserVar(string value)
        {
            var m = Regex.Match(value, @"^(""?)(\$(?:R?\d))(.*)$");
            if (m.Success && userVars.TryGetValue(m.Groups[2].Value, out var literal))
                return m.Groups[1].Value + literal + m.Groups[3].Value;
            return value;
        }

        void AddCandidate(string value)
        {
            if (string.IsNullOrEmpty(value)) { lastInstDirResolvable = false; return; }
            // One level of indirection: $0..$9 / $R0..$R9 hold the last literal
            // assigned earlier in the walk (electron-builder builds $INSTDIR from $0).
            var indirect = Regex.Match(value, @"^(\$(?:R?\d))(\\.*)?$");
            if (indirect.Success && userVars.TryGetValue(indirect.Groups[1].Value, out var literal))
                value = literal + indirect.Groups[2].Value;
            // StrCpy $INSTDIR "$INSTDIR\<suffix>" extends the assignment right before it.
            var suffixMatch = Regex.Match(value, @"^\$INSTDIR(\\.*)$");
            if (suffixMatch.Success)
            {
                var suffix = suffixMatch.Groups[1].Value;
                if (instDirAssignments.Count > 0 && lastInstDirResolvable)
                {
                    var k = instDirAssignments.Count - 1;
                    var firstSegment = suffix.TrimStart('\\').Split('\\')[0];
                    var lastSegment = instDirAssignments[k].Split('\\')[^1];
                    if (!string.IsNullOrEmpty(firstSegment) && lastSegment != firstSegment) instDirAssignments[k] += suffix;
                }
                return;
            }
            if (Unresolvable.IsMatch(value) || value.Contains("$INSTDIR", StringComparison.Ordinal)) { lastInstDirResolvable = false; return; }
            lastInstDirResolvable = true;
            instDirAssignments.Remove(value);
            instDirAssignments.Add(value);
        }

        for (var e = 0; e < ctx.EntriesCount; e++)
        {
            var b = ctx.EntriesOffset + (e * 28);
            var op = BitConverter.ToInt32(h, b);
            if (op != 13 && op != 25 && op != 51 && op != 62) continue;
            var p0 = BitConverter.ToInt32(h, b + 4);
            var p1 = BitConverter.ToInt32(h, b + 8);
            var p2 = BitConverter.ToInt32(h, b + 12);
            var p3 = BitConverter.ToInt32(h, b + 16);
            switch (op)
            {
                case 13:
                {
                    regView.Apply(p0, p2, () => ctx.Str(p1));
                    if (p0 == 1 && p2 == 0 && int.TryParse(ctx.Str(p1), out var context) && context != 0) allUsersSeen = true;
                    break;
                }
                case 25:
                {
                    if (p0 < 20)
                    {
                        var assigned = ctx.Str(p1);
                        var name = p0 < 10 ? "$" + p0 : "$R" + (p0 - 10);
                        if (!string.IsNullOrEmpty(assigned) && !Unresolvable.IsMatch(assigned) && p2 == 0 && p3 == 0) userVars[name] = assigned;
                        break;
                    }
                    if (p0 != 21 || p2 != 0 || p3 != 0) break;
                    AddCandidate(ctx.Str(p1));
                    break;
                }
                case 62:
                {
                    if (string.IsNullOrEmpty(uninstallerPath))
                    {
                        var candidate = ResolveUserVar(ctx.Str(p0));
                        if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) uninstallerPath = candidate;
                    }
                    break;
                }
                case 51:
                {
                    var subkey = ResolveUserVar(ctx.Str(p1));
                    if (!Regex.IsMatch(subkey, @"(?i)^Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\")) break;
                    var index = arpCandidates.FindIndex(c => c.Subkey == subkey);
                    if (index < 0)
                    {
                        var rootInfo = ResolveRegistryRoot(p0);
                        var rootName = rootInfo.Name is "HKCU" or "HKLM" or "SHCTX" ? rootInfo.Name : "";
                        arpCandidates.Add((subkey, rootName, rootInfo.View, regView.Is64, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                        index = arpCandidates.Count - 1;
                    }
                    var valueName = ctx.Str(p2);
                    if (string.IsNullOrEmpty(valueName)) break;
                    var values = arpCandidates[index].Values;
                    if (!values.ContainsKey(valueName)) values[valueName] = ctx.Str(p3);
                    break;
                }
            }
        }

        var arpSubkey = "";
        var arpRoot = "";
        var arpView64 = false;
        var arpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var regViewFromInit = false;
        if (arpCandidates.Count > 0)
        {
            // A literal key that carries DisplayName wins over a literal key
            // without it, which wins over a key built from a variable.
            var pick = arpCandidates.FirstOrDefault(c => !c.Subkey.Contains('$') && c.Values.ContainsKey("DisplayName"));
            if (pick.Subkey is null) pick = arpCandidates.FirstOrDefault(c => !c.Subkey.Contains('$'));
            if (pick.Subkey is null) pick = arpCandidates[0];
            arpSubkey = pick.Subkey;
            arpRoot = pick.Root;
            arpValues = pick.Values;
            // View: a forced root (HKLM64) decides; otherwise the SetRegView state at
            // the write in code order, except that a script whose last SetRegView is
            // 64 and that never selects 32 runs that statement in .onInit before any
            // section, so the key lands in the 64-bit view.
            if (pick.ForcedView == "64") arpView64 = true;
            else if (pick.ForcedView == "32") arpView64 = false;
            else if (pick.SeqView64) arpView64 = true;
            else if (regView.InitSets64) { arpView64 = true; regViewFromInit = true; }
        }
        meta.ArpValues = arpValues;
        meta.InstallDirCandidates = [.. instDirAssignments];

        var elevates = meta.RequestedExecutionLevel is "requireAdministrator" or "highestAvailable";
        var arpRootRaw = arpRoot;

        // A compile-time InstallDir that is neither a folder constant nor an
        // absolute path is a placeholder the script overwrites at run time; the
        // StrCpy candidate that matches the elevation model stands in for it.
        var resolvedFromCandidate = false;
        if (!Regex.IsMatch(meta.InstallDir, @"^(\$[A-Z]|[A-Za-z]:\\)") && instDirAssignments.Count > 0)
        {
            string? pick = null;
            if (elevates || arpRoot == "HKLM") pick = instDirAssignments.FirstOrDefault(v => MachineFolders.IsMatch(v));
            pick ??= instDirAssignments.FirstOrDefault(v => UserFolders.IsMatch(v));
            pick ??= instDirAssignments[0];
            meta.InstallDir = pick;
            resolvedFromCandidate = true;
        }

        // SHCTX resolves through the shell context in force when the section
        // runs: a script that switches to all users and either elevates or
        // targets a machine folder registers under HKLM.
        if (arpRoot == "SHCTX")
        {
            var machineTarget = MachineFolders.IsMatch(meta.InstallDir) || DriveOrShare.IsMatch(meta.InstallDir);
            arpRoot = allUsersSeen && (elevates || machineTarget) ? "HKLM" : "HKCU";
        }

        if (string.IsNullOrEmpty(uninstallerPath) && arpValues.TryGetValue("UninstallString", out var uninstallString))
        {
            var u = ResolveUserVar(uninstallString);
            var m = Regex.Match(u, @"^\s*""([^""]+\.exe)""");
            if (!m.Success) m = Regex.Match(u, @"^\s*(\S+\.exe)");
            if (m.Success) uninstallerPath = m.Groups[1].Value;
        }
        // A relative uninstaller path is written into the current output folder, which the sections set to $INSTDIR.
        if (!string.IsNullOrEmpty(uninstallerPath) && !Regex.IsMatch(uninstallerPath, @"^(\$|[A-Za-z]:\\|\\\\)"))
            uninstallerPath = "$INSTDIR\\" + uninstallerPath;
        meta.UninstallerPath = uninstallerPath;

        // SetShellVarContext all turns a compile-time user folder into its
        // all-users counterpart at run time; a user folder picked among the
        // script's run-time candidates belongs to the per-user branch.
        var userFolderChosen = UserFoldersWide.IsMatch(meta.InstallDir);
        var mapAllUsers = allUsersSeen && !(userFolderChosen && resolvedFromCandidate);
        meta.ShellVarContext = mapAllUsers ? "all" : "current";

        static string Literal(string? v) => v is not null && !v.Contains('$') ? v : "";
        if (arpValues.TryGetValue("DisplayName", out var dn)) meta.DisplayName = Literal(dn);
        if (arpValues.TryGetValue("DisplayVersion", out var dv)) meta.DisplayVersion = Literal(dv);
        if (arpValues.TryGetValue("Publisher", out var pub)) meta.Publisher = Literal(pub);
        if (string.IsNullOrEmpty(meta.DisplayName) && !string.IsNullOrEmpty(meta.Name) && !meta.Name.Contains('$')) meta.DisplayName = meta.Name;

        InstallBranch ResolveBranch(string installDir, bool allUsersFolders, string root, string modeSwitch)
        {
            var branch = new InstallBranch
            {
                InstallDir = installDir,
                UninstallerPath = uninstallerPath,
                InstallArgs = ("/S " + modeSwitch).Trim()
            };
            var dirWindows = ToWindowsPath(installDir, "", allUsersFolders);
            branch.InstallDirWindows = dirWindows;
            var dirResolved = Regex.IsMatch(dirWindows, @"^(%[^%]+%|[A-Za-z]:\\)") && !dirWindows.Contains('$');
            branch.Resolved = dirResolved;
            if (!string.IsNullOrEmpty(uninstallerPath))
            {
                branch.UninstallerPathWindows = ToWindowsPath(uninstallerPath, dirWindows, allUsersFolders);
                if (dirResolved && !branch.UninstallerPathWindows.Contains('$'))
                    branch.SilentUninstallCommand = "\"" + branch.UninstallerPathWindows + "\" " + branch.InstallArgs;
            }
            if (!string.IsNullOrEmpty(arpSubkey))
            {
                branch.RegistryHive = root;
                branch.RegistryView = root != "HKLM" ? "" : arpView64 ? "64" : "32";
                var keyName = arpSubkey;
                if (keyName.Contains('$'))
                {
                    branch.UninstallRegistryKeyNote = "ARP key name is computed at run time: " + keyName;
                }
                else
                {
                    if (root == "HKLM" && !arpView64)
                    {
                        keyName = Regex.Replace(keyName, @"(?i)^Software\\", @"Software\WOW6432Node\");
                        branch.UninstallRegistryKeyNote = "32-bit installer without SetRegView 64: the key lands under WOW6432Node on x64 Windows.";
                    }
                    else if (root == "HKLM" && regViewFromInit)
                    {
                        branch.UninstallRegistryKeyNote = "SetRegView 64 runs in .onInit before the key is written; the key is in the 64-bit view.";
                    }
                    else if (root == "HKCU")
                    {
                        branch.UninstallRegistryKeyNote = "Per-user registration under HKCU; detection and uninstall must run in the user context.";
                    }
                    if (!string.IsNullOrEmpty(root)) branch.UninstallRegistryKey = root + ":\\" + keyName;
                }
            }
            // Where the files land decides the context: a Program Files target
            // needs the machine context even when the script registers under HKCU.
            var userFolder = UserFoldersWide.IsMatch(installDir);
            if (MachineFolders.IsMatch(installDir) || DriveOrShare.IsMatch(installDir)) branch.InstallContext = "PerMachine";
            else if (userFolder) branch.InstallContext = "PerUser";
            else if (root == "HKCU") branch.InstallContext = "PerUser";
            else if (root == "HKLM" || elevates) branch.InstallContext = "PerMachine";
            else if (meta.RequestedExecutionLevel == "asInvoker") branch.InstallContext = "PerUser";
            return branch;
        }

        var defaultBranch = ResolveBranch(meta.InstallDir, mapAllUsers, arpRoot, "");
        meta.InstallDirWindows = defaultBranch.InstallDirWindows;
        meta.UninstallerPathWindows = defaultBranch.UninstallerPathWindows;
        meta.SilentUninstallCommand = defaultBranch.SilentUninstallCommand;
        meta.UninstallRegistryKey = defaultBranch.UninstallRegistryKey;
        meta.UninstallRegistryKeyNote = defaultBranch.UninstallRegistryKeyNote;
        meta.RegistryHive = defaultBranch.RegistryHive;
        meta.RegistryView = defaultBranch.RegistryView;
        meta.InstallContext = defaultBranch.InstallContext;
        if (resolvedFromCandidate)
            meta.Note = "Install directory is assigned at run time; using " + meta.InstallDir + " of " + string.Join(" | ", instDirAssignments) + ".";
        else if (!defaultBranch.Resolved && !string.IsNullOrEmpty(meta.InstallDir))
            meta.Note = "Install directory is not resolvable before install: " + meta.InstallDir;

        // Mode switches: electron-builder and MultiUser scripts accept /allusers
        // and /currentuser (case-insensitive) and keep one StrCpy candidate per branch.
        var allUsersMatch = Regex.Match(ctx.Table, "(?i)/allusers");
        if (allUsersMatch.Success) meta.AllUsersSwitch = allUsersMatch.Value;
        var currentUserMatch = Regex.Match(ctx.Table, "(?i)/currentuser");
        if (currentUserMatch.Success) meta.CurrentUserSwitch = currentUserMatch.Value;

        var defaultMode = meta.InstallContext == "PerMachine" ? "AllUsers" : "CurrentUser";
        meta.InstallMode = defaultMode;
        var machineDir = instDirAssignments.FirstOrDefault(v => MachineFolders.IsMatch(v));
        if (machineDir is null && MachineFolders.IsMatch(meta.InstallDir)) machineDir = meta.InstallDir;
        var userDir = instDirAssignments.FirstOrDefault(v => UserFolders.IsMatch(v));
        if (userDir is null && userFolderChosen) userDir = meta.InstallDir;

        var variants = new Dictionary<string, InstallBranch>(StringComparer.OrdinalIgnoreCase) { [defaultMode] = defaultBranch };
        if (defaultMode == "CurrentUser" && machineDir is not null && !string.IsNullOrEmpty(meta.AllUsersSwitch))
        {
            var root = arpRootRaw == "SHCTX" ? "HKLM" : arpRoot;
            variants["AllUsers"] = ResolveBranch(machineDir, true, root, meta.AllUsersSwitch);
        }
        else if (defaultMode == "AllUsers" && userDir is not null && !string.IsNullOrEmpty(meta.CurrentUserSwitch))
        {
            var root = arpRootRaw == "SHCTX" ? "HKCU" : arpRoot;
            variants["CurrentUser"] = ResolveBranch(userDir, false, root, meta.CurrentUserSwitch);
        }
        meta.ModeVariants = variants;
        meta.InstallModes = [.. new[] { "CurrentUser", "AllUsers" }.Where(variants.ContainsKey)];

        return meta;
    }
}
