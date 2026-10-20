using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>What the compiled [Setup] section of an Inno Setup installer says about the install.</summary>
public sealed class InnoSetupMetadata
{
    public bool HeaderAvailable { get; set; }
    /// <summary>Data version with its character width, for example "6.7.0 (Unicode)".</summary>
    public string DataVersion { get; set; } = "";
    public string Compression { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string Publisher { get; set; } = "";
    /// <summary>What Programs and Features shows: UninstallDisplayName, else AppVerName, else name and version.</summary>
    public string ArpDisplayName { get; set; } = "";
    public string DefaultDirName { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public string InstallDirWindows { get; set; } = "";
    public string UninstallFilesDir { get; set; } = "";
    public string UninstallerPath { get; set; } = "";
    public string UninstallerPathWindows { get; set; } = "";
    public string SilentUninstallCommand { get; set; } = "";
    public string UninstallRegistryKey { get; set; } = "";
    public string UninstallRegistryKeyNote { get; set; } = "";
    public string RegistryHive { get; set; } = "";
    public string RegistryView { get; set; } = "";
    public Dictionary<string, string> ArpValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>none, poweruser, admin or lowest; empty when the fixed fields did not decode.</summary>
    public string PrivilegesRequired { get; set; } = "";
    public List<string> PrivilegesRequiredOverridesAllowed { get; set; } = [];
    public string ArchitecturesAllowed { get; set; } = "";
    public string ArchitecturesInstallIn64BitMode { get; set; } = "";
    public bool Is64BitInstallMode { get; set; }
    public string CreateUninstallRegKey { get; set; } = "";
    public string Uninstallable { get; set; } = "";
    public string MinWindowsVersion { get; set; } = "";
    public string RequestedExecutionLevel { get; set; } = "";
    public string InstallContext { get; set; } = "";
    public string InstallMode { get; set; } = "";
    public List<string> InstallModes { get; set; } = [];
    public string AllUsersSwitch { get; set; } = "";
    public string CurrentUserSwitch { get; set; } = "";
    public Dictionary<string, InstallBranch> ModeVariants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Note { get; set; } = "";
}

/// <summary>
/// Reads an Inno Setup installer's compiled [Setup] header. The SetupLdr stub
/// carries an offset table (RCDATA 11111) that points at a 64-byte data-version
/// string followed by a CRC-chunked, LZMA1-compressed block whose first record
/// is TSetupHeader: the [Setup] strings, then fixed fields whose layout changes
/// between data versions and is read by version.
/// </summary>
public static class InnoSetupReader
{
    private const string SilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    private static readonly byte[][] LoaderMagics =
    [
        [0x72, 0x44, 0x6C, 0x50, 0x74, 0x53, 0xCD, 0xE6, 0xD7, 0x7B, 0x0B, 0x2A],
        [0x6E, 0x53, 0x35, 0x57, 0x37, 0x64, 0x54, 0x83, 0xAA, 0x1B, 0x0F, 0x6A]
    ];

    private static readonly string[] PrivilegesNames = ["none", "poweruser", "admin", "lowest"];
    private static readonly string[] CompressionNames = ["none", "zip", "bzip", "lzma", "lzma2"];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>The largest stored setup-0 block the reader loads.</summary>
    public const long MaxStoredHeaderBytes = 64L * 1024 * 1024;

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(byte[] data, int offset, int length)
    {
        var c = 0xFFFFFFFFu;
        var end = offset + length;
        for (var i = offset; i < end; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// A block is a run of chunks, each a CRC32 followed by up to 4096 payload
    /// bytes; only the last chunk may be shorter. Returns the concatenated
    /// payloads; crcOk reports whether every chunk matched.
    /// </summary>
    public static byte[] Unchunk(byte[] data, int offset, int storedSize, out bool crcOk)
    {
        crcOk = true;
        var end = Math.Min(data.Length, offset + Math.Max(0, storedSize));
        var result = new byte[Math.Max(0, end - offset)];
        var outPos = 0;
        var pos = offset;
        while (pos + 4 < end)
        {
            var expected = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            pos += 4;
            var len = Math.Min(4096, end - pos);
            if (Crc32(data, pos, len) != expected) crcOk = false;
            Buffer.BlockCopy(data, pos, result, outPos, len);
            outPos += len;
            pos += len;
        }
        if (outPos != result.Length)
        {
            var trimmed = new byte[outPos];
            Buffer.BlockCopy(result, 0, trimmed, 0, outPos);
            return trimmed;
        }
        return result;
    }

    /// <summary>The setup-0 (header) and setup-1 (data) offsets from the SetupLdr table.</summary>
    public sealed class LoaderOffsets
    {
        public int Revision;
        public long ExeOffset;
        public long ExeUncompressedSize;
        public long HeaderOffset;
        public long DataOffset;
        public bool TableCrcOk;
    }

    private static LoaderOffsets? ParseLoaderTable(byte[] bytes, int offset)
    {
        if (bytes.Length < offset + 44) return null;
        var matched = LoaderMagics.Any(magic =>
        {
            for (var i = 0; i < 12; i++) if (bytes[offset + i] != magic[i]) return false;
            return true;
        });
        if (!matched) return null;
        uint U32(int at) => BitConverter.ToUInt32(bytes, offset + at);
        long I64(int at) => BitConverter.ToInt64(bytes, offset + at);
        var revision = U32(12);
        // Revision 1 (SetupLdr 5.1.5 to 6.4) stores 32-bit offsets in 44 bytes;
        // revision 2 (6.5 and later) widens the offsets to 64-bit in 64 bytes.
        if (revision == 1)
        {
            return new LoaderOffsets
            {
                Revision = 1,
                ExeOffset = U32(20),
                ExeUncompressedSize = U32(24),
                HeaderOffset = U32(32),
                DataOffset = U32(36),
                TableCrcOk = Crc32(bytes, offset, 40) == U32(40)
            };
        }
        if (revision == 2)
        {
            if (bytes.Length < offset + 64) return null;
            return new LoaderOffsets
            {
                Revision = 2,
                ExeOffset = I64(24),
                ExeUncompressedSize = U32(32),
                HeaderOffset = I64(40),
                DataOffset = I64(48),
                TableCrcOk = Crc32(bytes, offset, 60) == U32(60)
            };
        }
        return null;
    }

    /// <summary>
    /// Locates the offset table: the RCDATA 11111 resource, or for stubs older
    /// than SetupLdr 5.1.5 a bounded scan of the stub for the table magic.
    /// </summary>
    public static LoaderOffsets? ReadLoaderOffsets(string path)
    {
        var resource = PeResources.Read(path, PeResources.RtRcData, 11111, 4096);
        if (resource is not null)
        {
            var table = ParseLoaderTable(resource, 0);
            if (table is not null) return table;
        }

        byte[] buffer;
        using (var fs = File.OpenRead(path))
        {
            var take = (int)Math.Min(fs.Length, 8L * 1024 * 1024);
            buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buffer, read, take - read);
                if (n <= 0) break;
                read += n;
            }
        }
        var text = Encoding.Latin1.GetString(buffer);
        foreach (var magic in LoaderMagics)
        {
            var needle = Encoding.Latin1.GetString(magic);
            var at = text.IndexOf(needle, StringComparison.Ordinal);
            while (at >= 0)
            {
                var table = ParseLoaderTable(buffer, at);
                if (table is not null && table.TableCrcOk) return table;
                at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
            }
        }
        return null;
    }

    /// <summary>The parsed "Inno Setup Setup Data (x.y.z[.r])[ (u)]" signature.</summary>
    public sealed class DataVersion
    {
        public int Major, Minor, Build, Revision;
        /// <summary>major*1e9 + minor*1e6 + build*1e3 + revision, comparable as one integer.</summary>
        public long Value;
        public bool Unicode;
        public string Text = "";
    }

    public static long Ver(int a, int b, int c, int d = 0) => a * 1000000000L + b * 1000000L + c * 1000L + d;

    /// <summary>Data versions 6.3.0 and later are Unicode without carrying the "(u)" marker.</summary>
    public static DataVersion? ParseDataVersion(string text)
    {
        var m = Regex.Match(text, @"^Inno Setup Setup Data \((\d+)\.(\d+)\.(\d+)(?:\.(\d+))?\)(?<u>\s*\((u|U)\))?");
        if (!m.Success) return null;
        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value);
        var build = int.Parse(m.Groups[3].Value);
        var rev = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
        var value = Ver(major, minor, build, rev);
        return new DataVersion
        {
            Major = major, Minor = minor, Build = build, Revision = rev, Value = value,
            Unicode = value >= Ver(6, 3, 0) || m.Groups["u"].Success,
            Text = $"{major}.{minor}.{build}" + (rev != 0 ? $".{rev}" : "")
        };
    }

    private sealed class HeaderBlock
    {
        public DataVersion? Version;
        public string VersionText = "";
        public byte[]? Bytes;
        public string Compression = "";
        public bool CrcOk;
        public string Note = "";
    }

    /// <summary>
    /// Reads the setup-0 block at headerOffset and returns the decoded leading
    /// bytes of the TSetupHeader record. The block header is CRC32 + stored
    /// size + compressed flag; the body is CRC-chunked. A compressed body is
    /// LZMA1 with a 5-byte properties prefix and no size field, so decoding
    /// stops after maxOutput bytes; callers retry with a larger budget when
    /// the record is longer.
    /// </summary>
    private static HeaderBlock? ExpandHeaderBlock(string path, long headerOffset, int maxOutput, CancellationToken ct = default)
    {
        DataVersion? version;
        string idText;
        byte[] raw;
        bool compressed;

        using (var fs = File.OpenRead(path))
        {
            if (headerOffset < 0 || headerOffset + 64 + 9 > fs.Length) return null;
            fs.Position = headerOffset;
            using var br = new BinaryReader(fs);
            var idBytes = br.ReadBytes(64);
            idText = Encoding.ASCII.GetString(idBytes).TrimEnd('\0');
            version = ParseDataVersion(idText);
            HeaderBlock Fail(string note, bool crcOk = false, string compression = "") =>
                new() { Version = version, VersionText = idText, Compression = compression, CrcOk = crcOk, Note = note };
            if (version is null) return Fail("Unrecognised setup data signature: " + idText);
            if (version.Value < Ver(4, 0, 9)) return Fail("Setup data version " + version.Text + " predates the block format this reader supports.");

            // 6.5.0 moved the password material out of TSetupHeader into a
            // CRC-prefixed TSetupEncryptionHeader (49 bytes) that precedes the
            // block; EncryptionUse=euFull means the block itself is encrypted.
            if (version.Value >= Ver(6, 5, 0))
            {
                if (fs.Position + 53 > fs.Length) return Fail("Header block is truncated.");
                var encHeader = br.ReadBytes(53);
                var encExpected = BitConverter.ToUInt32(encHeader, 0);
                if (Crc32(encHeader, 4, 49) != encExpected) return Fail("Encryption header checksum mismatch.");
                if (encHeader[4] == 2) return Fail("Setup header is encrypted (EncryptionUse=full); a password is required to read it.", crcOk: true);
            }

            // Block header: CRC32 over StoredSize + Compressed; StoredSize is a
            // LongWord before 6.7.0 and an Int64 from 6.7.0.
            var sizeWidth = version.Value >= Ver(6, 7, 0) ? 8 : 4;
            var blockHeader = br.ReadBytes(4 + sizeWidth + 1);
            if (blockHeader.Length < 4 + sizeWidth + 1) return Fail("Header block is truncated.");
            var expectedCrc = BitConverter.ToUInt32(blockHeader, 0);
            var actualCrc = Crc32(blockHeader, 4, sizeWidth + 1);
            var storedSize = sizeWidth == 8 ? BitConverter.ToInt64(blockHeader, 4) : BitConverter.ToUInt32(blockHeader, 4);
            compressed = blockHeader[4 + sizeWidth] != 0;
            if (actualCrc != expectedCrc) return Fail("Header block checksum mismatch.");
            var available = fs.Length - fs.Position;
            if (storedSize < 0 || storedSize > MaxStoredHeaderBytes) return Fail("The setup header block size field is larger than the 64 MB limit; the header was not decoded.", crcOk: true);
            var take = (int)Math.Min(storedSize, available);
            raw = br.ReadBytes(take);
        }

        var payload = Unchunk(raw, 0, raw.Length, out var chunkCrcOk);
        var result = new HeaderBlock { Version = version, VersionText = idText, Compression = "stored", CrcOk = chunkCrcOk };
        if (!compressed)
        {
            result.Bytes = payload;
            return result;
        }
        if (version.Value < Ver(4, 1, 6))
        {
            result.Compression = "zlib";
            result.Note = "zlib header blocks are not decoded.";
            return result;
        }
        result.Compression = "lzma1";
        if (payload.Length < 6) { result.Note = "Header block is truncated."; return result; }
        var props = payload[0];
        var dict = BitConverter.ToUInt32(payload, 1);
        if (dict < 4096) dict = 4096;
        result.Bytes = LzmaDecoder.Decode(payload, 5, payload.Length - 5, props, dict, maxOutput, out _, ct);
        return result;
    }

    private sealed class SetupHeader
    {
        public readonly Dictionary<string, string> Strings = new(StringComparer.Ordinal);
        public int StringsEnd;
        public bool FixedDecoded;
        public string FixedNote = "";
        public string MinWindowsVersion = "";
        public string PrivilegesRequired = "";
        public List<string> PrivilegesRequiredOverridesAllowed = [];
        public string CompressMethod = "";
        public int NumLanguageEntries;
        public int NumFileEntries;
        public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);
        public int FixedEnd;
        public string? ArchitecturesAllowed;
        public string? ArchitecturesInstallIn64BitMode;

        public string Get(string name) => Strings.TryGetValue(name, out var v) ? v : "";
    }

    /// <summary>
    /// Parses the leading TSetupHeader record from a decoded block. Strings
    /// are read for every data version from 5.0.0; the fixed part is read by
    /// version and accepted only when every enum lands in range. Throws
    /// EndOfStreamException when the buffer ends inside the record.
    /// </summary>
    private static SetupHeader ParseSetupHeader(byte[] data, DataVersion version)
    {
        var v = version.Value;
        var pos = 0;
        var len = data.Length;
        void Need(int n) { if (pos + n > len) throw new EndOfStreamException("Setup header record extends past the decoded block."); }
        int U8() { Need(1); return data[pos++]; }
        long U32() { Need(4); var x = BitConverter.ToUInt32(data, pos); pos += 4; return x; }
        long I32() { Need(4); var x = BitConverter.ToInt32(data, pos); pos += 4; return x; }
        void Skip(int n) { Need(n); pos += n; }
        var encoding = version.Unicode ? Encoding.Unicode : Encoding.Latin1;
        string Str(bool ansi = false, bool binary = false)
        {
            var n = U32();
            if (n > 64L * 1024 * 1024) throw new InvalidDataException("Setup header string length is implausible.");
            Need((int)n);
            var text = binary ? "" : ansi ? Encoding.Latin1.GetString(data, pos, (int)n) : encoding.GetString(data, pos, (int)n);
            pos += (int)n;
            return text;
        }

        var h = new SetupHeader();
        var s = h.Strings;
        s["AppName"] = Str();
        s["AppVerName"] = Str();
        s["AppId"] = Str();
        s["AppCopyright"] = Str();
        s["AppPublisher"] = Str();
        s["AppPublisherURL"] = Str();
        if (v >= Ver(5, 1, 13)) s["AppSupportPhone"] = Str();
        s["AppSupportURL"] = Str();
        s["AppUpdatesURL"] = Str();
        s["AppVersion"] = Str();
        s["DefaultDirName"] = Str();
        s["DefaultGroupName"] = Str();
        s["BaseFilename"] = Str();
        if (v < Ver(5, 2, 5)) { Str(ansi: true); Str(ansi: true); Str(ansi: true); }
        s["UninstallFilesDir"] = Str();
        s["UninstallDisplayName"] = Str();
        s["UninstallDisplayIcon"] = Str();
        s["AppMutex"] = Str();
        s["DefaultUserInfoName"] = Str();
        s["DefaultUserInfoOrg"] = Str();
        s["DefaultUserInfoSerial"] = Str();
        if (v < Ver(5, 2, 5)) Str(binary: true);
        s["AppReadmeFile"] = Str();
        s["AppContact"] = Str();
        s["AppComments"] = Str();
        s["AppModifyPath"] = Str();
        if (v >= Ver(5, 3, 8)) s["CreateUninstallRegKey"] = Str();
        if (v >= Ver(5, 3, 10)) s["Uninstallable"] = Str();
        if (v >= Ver(5, 5, 0)) s["CloseApplicationsFilter"] = Str();
        if (v >= Ver(5, 5, 6)) s["SetupMutex"] = Str();
        if (v >= Ver(5, 6, 1)) { s["ChangesEnvironment"] = Str(); s["ChangesAssociations"] = Str(); }
        if (v >= Ver(6, 3, 0)) { h.ArchitecturesAllowed = Str(); h.ArchitecturesInstallIn64BitMode = Str(); }
        if (v >= Ver(6, 4, 2)) s["CloseApplicationsFilterExcludes"] = Str();
        if (v >= Ver(6, 5, 0)) s["SevenZipLibraryName"] = Str();
        if (v >= Ver(6, 7, 0)) { s["UsePreviousAppDir"] = Str(); Str(); Str(); Str(); Str(); }
        if (v >= Ver(5, 2, 5)) { Str(ansi: true); Str(ansi: true); Str(ansi: true); }
        if (v >= Ver(5, 2, 1) && v < Ver(5, 3, 10)) Str(binary: true);
        if (v >= Ver(5, 2, 5)) Str(binary: true);
        h.StringsEnd = pos;

        if (v < Ver(5, 0, 0)) { h.FixedNote = "Fixed header fields are not read for data version " + version.Text + "."; return h; }

        if (!version.Unicode) Skip(32);
        var countNames = new List<string> { "NumLanguageEntries", "NumCustomMessageEntries", "NumPermissionEntries", "NumTypeEntries", "NumComponentEntries", "NumTaskEntries", "NumDirEntries" };
        if (v >= Ver(6, 5, 0)) countNames.Add("NumISSigKeyEntries");
        countNames.AddRange(["NumFileEntries", "NumFileLocationEntries", "NumIconEntries", "NumIniEntries", "NumRegistryEntries", "NumInstallDeleteEntries", "NumUninstallDeleteEntries", "NumRunEntries", "NumUninstallRunEntries"]);
        var counts = new Dictionary<string, long>();
        foreach (var name in countNames) counts[name] = I32();
        foreach (var (name, count) in counts) h.Counts[name] = count;
        h.NumLanguageEntries = (int)counts["NumLanguageEntries"];
        h.NumFileEntries = (int)counts["NumFileEntries"];
        if (v >= Ver(7, 0, 0)) U32();

        // MinVersion / OnlyBelowVersion: WinVersion (build u16, minor u8, major u8), NTVersion (same), NTServicePack (minor u8, major u8).
        Need(20);
        h.MinWindowsVersion = $"{data[pos + 7]}.{data[pos + 6]}.{BitConverter.ToUInt16(data, pos + 4)}";
        Skip(20);

        if (v < Ver(6, 4, 0, 1)) Skip(8);
        if (v < Ver(5, 5, 7)) Skip(4);
        if (v >= Ver(2, 0, 0) && v < Ver(5, 0, 4)) Skip(4);
        int wizardStyle = 0, darkStyle = 0, lightStyling = 0;
        if (v >= Ver(6, 0, 0) && v < Ver(6, 6, 0)) wizardStyle = U8();
        if (v >= Ver(6, 0, 0)) Skip(8);
        if (v >= Ver(6, 6, 0)) darkStyle = U8();
        var alpha = 0;
        if (v >= Ver(5, 5, 7)) alpha = U8();
        if (v >= Ver(6, 5, 2) && v < Ver(6, 6, 0)) Skip(8);
        if (v >= Ver(6, 6, 0) && v < Ver(6, 7, 0)) Skip(16);
        if (v >= Ver(6, 6, 1) && v < Ver(6, 7, 0)) Skip(1);
        if (v >= Ver(6, 7, 0)) { Skip(26); lightStyling = U8(); }
        if (v >= Ver(6, 5, 0)) { }
        else if (v >= Ver(6, 4, 0)) Skip(48);
        else if (v >= Ver(5, 3, 9)) Skip(28);
        else if (v >= Ver(4, 2, 2)) Skip(24);
        else Skip(16);
        Skip(8);
        var slices = I32();
        var logMode = U8();
        var dirExists = U8();
        var privileges = U8();
        var overrides = 0;
        if (v >= Ver(5, 7, 0)) overrides = U8();
        var showLang = U8();
        var langDetect = U8();
        var compress = U8();
        int archAllowedFlags = 0, arch64Flags = 0;
        if (v >= Ver(5, 1, 0) && v < Ver(6, 3, 0)) { archAllowedFlags = U8(); arch64Flags = U8(); }
        if (v >= Ver(5, 2, 1) && v < Ver(5, 3, 10)) Skip(8);
        int disableDir = 0, disableGroup = 0;
        if (v >= Ver(5, 3, 3)) { disableDir = U8(); disableGroup = U8(); }
        h.FixedEnd = pos;

        var sane = privileges <= 3 && overrides <= 3 && logMode <= 2 && dirExists <= 2 &&
                   showLang <= 2 && langDetect <= 2 && compress <= 4 && wizardStyle <= 1 &&
                   darkStyle <= 2 && alpha <= 2 && lightStyling <= 2 && disableDir <= 2 && disableGroup <= 2 &&
                   slices >= 1 && slices <= 100000 && h.NumLanguageEntries >= 1 && h.NumLanguageEntries <= 1000 &&
                   h.NumFileEntries >= 0 && h.NumFileEntries <= 10000000;
        if (!sane)
        {
            h.FixedNote = "Fixed header fields of data version " + version.Text + " did not validate; PrivilegesRequired and install mode are not reported.";
            return h;
        }

        h.FixedDecoded = true;
        h.PrivilegesRequired = PrivilegesNames[privileges];
        if ((overrides & 1) != 0) h.PrivilegesRequiredOverridesAllowed.Add("commandline");
        if ((overrides & 2) != 0) h.PrivilegesRequiredOverridesAllowed.Add("dialog");
        h.CompressMethod = CompressionNames[compress];
        if (v < Ver(6, 3, 0))
        {
            // Flag bits follow TSetupProcessorArchitecture: unknown, x86, x64, ia64 (arm32 in 6.x), arm64.
            string[] archNames = ["unknown", "x86", "x64", "ia64", "arm64"];
            string ToNames(int flags) => string.Join(" ", Enumerable.Range(0, 5).Where(i => (flags & (1 << i)) != 0).Select(i => archNames[i]));
            h.ArchitecturesAllowed = ToNames(archAllowedFlags);
            h.ArchitecturesInstallIn64BitMode = ToNames(arch64Flags);
        }
        return h;
    }

    private static readonly Dictionary<long, string> RegistryRootNames = new()
    {
        [1] = "HKA", [0x80000000] = "HKCR", [0x80000001] = "HKCU", [0x80000002] = "HKLM",
        [0x80000003] = "HKU", [0x80000004] = "HKPD", [0x80000005] = "HKCC", [0x80000006] = "HKDD"
    };
    private static readonly string[] RegistryTypeNames = ["", "REG_SZ", "REG_EXPAND_SZ", "REG_DWORD", "REG_BINARY", "REG_MULTI_SZ", "REG_QWORD"];
    private static readonly string[] RegistryFlagNames =
        ["createvalueifdoesntexist", "uninsdeletevalue", "uninsclearvalue", "uninsdeletekey", "uninsdeletekeyifempty",
         "preservestringtype", "deletekey", "deletevalue", "noerror", "dontcreatekey", "32bit", "64bit"];
    private static readonly string[] BitnessNames = ["", "32", "64", "native", "process"];

    /// <summary>
    /// Lists the compiled [Registry] entries. The entry lists follow TSetupHeader in the
    /// setup-0 block; each is a run of records made of length-prefixed strings and a
    /// fixed part whose size depends on the data version. Layouts are known from data
    /// version 6.0.0; older or encrypted setups give no rows and a note. Values keep
    /// their constants ({app}, {code:...}); HKA is HKLM for an administrative install
    /// and HKCU for a per-user one.
    /// </summary>
    public static (List<RegistryWrite> Rows, string Note) ReadRegistryEntries(string path, int maxRows, CancellationToken ct = default)
    {
        var rows = new List<RegistryWrite>();
        var offsets = ReadLoaderOffsets(path);
        if (offsets is null) return (rows, "SetupLdr offset table not found.");
        foreach (var budget in new[] { 4 << 20, 32 << 20, 160 << 20 })
        {
            ct.ThrowIfCancellationRequested();
            var block = ExpandHeaderBlock(path, offsets.HeaderOffset, budget, ct);
            if (block?.Bytes is null)
                return (rows, block is not null && !string.IsNullOrEmpty(block.Note) ? block.Note : "Setup header block could not be decoded.");
            try
            {
                var header = ParseSetupHeader(block.Bytes, block.Version!);
                var note = ReadRegistryList(block.Bytes, block.Version!, header, rows, maxRows, ct);
                return (rows, note);
            }
            catch (EndOfStreamException)
            {
                rows.Clear();
                if (block.Bytes.Length < budget) return (rows, "Setup entry lists end inside the decoded block.");
            }
            catch (InvalidDataException)
            {
                rows.Clear();
                return (rows, "Setup entry lists did not validate; registry entries are not reported.");
            }
        }
        return (rows, "Setup entry lists exceed the decode budget.");
    }

    private static string ReadRegistryList(byte[] data, DataVersion version, SetupHeader header, List<RegistryWrite> rows, int maxRows, CancellationToken ct)
    {
        var v = version.Value;
        if (v < Ver(6, 0, 0)) return "Registry entries are read for setup data 6.0.0 and later; this installer is " + version.Text + ".";
        if (!header.FixedDecoded) return "Registry entries follow header fields that did not decode.";

        var pos = header.FixedEnd;
        var len = data.Length;
        void Need(long n) { if (pos + n > len) throw new EndOfStreamException("Setup entry lists extend past the decoded block."); }
        void Skip(int n) { Need(n); pos += n; }
        int U8() { Need(1); return data[pos++]; }
        int U16() { Need(2); var x = BitConverter.ToUInt16(data, pos); pos += 2; return x; }
        long U32() { Need(4); var x = BitConverter.ToUInt32(data, pos); pos += 4; return x; }
        var encoding = version.Unicode ? Encoding.Unicode : Encoding.Latin1;
        string Str()
        {
            var n = U32();
            if (n > 64L * 1024 * 1024) throw new InvalidDataException("Setup entry string length is implausible.");
            Need(n);
            var text = encoding.GetString(data, pos, (int)n);
            pos += (int)n;
            return text;
        }
        long Count(string name) => header.Counts.TryGetValue(name, out var c) ? c : 0;
        void SkipList(long count, int strings, int fixedSize)
        {
            if (count < 0 || count > 10_000_000) throw new InvalidDataException("Setup entry count is implausible.");
            for (long i = 0; i < count; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                for (var s = 0; s < strings; s++)
                {
                    var n = U32();
                    if (n > 64L * 1024 * 1024) throw new InvalidDataException("Setup entry string length is implausible.");
                    Need(n);
                    pos += (int)n;
                }
                Skip(fixedSize);
            }
        }

        // Header tail: UninstallDisplaySize, then the Options set whose byte count follows
        // the option member count of each data version.
        var optionBytes = v >= Ver(6, 7, 0) ? 8 : v >= Ver(6, 4, 0, 1) ? 6 : v >= Ver(6, 3, 0) ? 7 : 6;
        Skip(8 + optionBytes);

        var v650 = v >= Ver(6, 5, 0);
        var v660 = v >= Ver(6, 6, 0);
        var v670 = v >= Ver(6, 7, 0);
        var v700 = v >= Ver(7, 0, 0);
        SkipList(Count("NumLanguageEntries"), (v660 ? 4 : 6) + 4, v660 ? 19 : 21);
        SkipList(Count("NumCustomMessageEntries"), 2, 4);
        SkipList(Count("NumPermissionEntries"), 1, 0);
        SkipList(Count("NumTypeEntries"), 4, 30);
        SkipList(Count("NumComponentEntries"), 5, v670 ? 39 : 42);
        SkipList(Count("NumTaskEntries"), 6, v670 ? 23 : 26);
        SkipList(Count("NumDirEntries"), 7, 27);
        if (v650) SkipList(Count("NumISSigKeyEntries"), 3, 0);
        SkipList(Count("NumFileEntries"), v650 ? 16 : 10, v700 ? 81 : v670 ? 80 : v650 ? 77 : 43);
        SkipList(Count("NumIconEntries"), 13, v >= Ver(6, 1, 0) ? 48 : 32);
        SkipList(Count("NumIniEntries"), 10, 21);

        var count = Count("NumRegistryEntries");
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("Registry entry count is implausible.");
        for (long i = 0; i < count; i++)
        {
            if ((i & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            var subkey = Str(); var valueName = Str(); var valueData = Str();
            var components = Str(); var tasks = Str(); var languages = Str(); var check = Str();
            Str(); Str();
            Skip(20);
            var rootKey = U32();
            Skip(2);
            var type = U8();
            var bitness = v700 ? U8() : 0;
            var options = U16();
            if (!RegistryRootNames.TryGetValue(rootKey, out var root) || type >= RegistryTypeNames.Length || bitness >= BitnessNames.Length)
                throw new InvalidDataException("A registry entry did not validate.");
            if (rows.Count >= maxRows) return $"Only the first {maxRows} registry entries are listed.";

            var flags = Enumerable.Range(0, RegistryFlagNames.Length).Where(b => (options & (1 << b)) != 0).Select(b => RegistryFlagNames[b]).ToList();
            var view = v700 ? BitnessNames[bitness] : flags.Contains("64bit") ? "64" : flags.Contains("32bit") ? "32" : "";
            var action = flags.Contains("deletekey") ? "delete key"
                : flags.Contains("deletevalue") ? (type != 0 ? "delete value, then write" : "delete value")
                : type == 0 ? "create key" : "write";
            var conditions = new List<string>();
            if (components.Length > 0) conditions.Add("Components: " + components);
            if (tasks.Length > 0) conditions.Add("Tasks: " + tasks);
            if (languages.Length > 0) conditions.Add("Languages: " + languages);
            if (check.Length > 0) conditions.Add("Check: " + check);
            rows.Add(new RegistryWrite("InnoSetup", root, subkey, valueName, valueData, RegistryTypeNames[type], string.Join("; ", conditions))
            {
                Action = action,
                View = view,
                Section = string.Join(" ", flags.Where(f => f is not "32bit" and not "64bit"))
            });
        }
        return "";
    }

    /// <summary>
    /// Rewrites Inno Setup directory constants into Windows environment form
    /// for the context a deployment runs in. {autopf} and the other auto*
    /// constants follow the install mode, Program Files follows is64BitMode,
    /// {app} expands to appDir when given. A path that keeps a run-time
    /// constant ({code:...}, {param:...}, {reg:...}) is returned with the
    /// constant intact.
    /// </summary>
    public static string ToWindowsPath(string path, bool is64BitMode = true, bool perUser = false, string appDir = "")
    {
        if (string.IsNullOrEmpty(path)) return "";
        var pf = is64BitMode ? "%ProgramFiles%" : "%ProgramFiles(x86)%";
        var cf = is64BitMode ? "%CommonProgramFiles%" : "%CommonProgramFiles(x86)%";
        const string userPf = @"%LOCALAPPDATA%\Programs";
        const string userCf = @"%LOCALAPPDATA%\Programs\Common";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["autopf"] = perUser ? userPf : pf,
            ["autopf32"] = perUser ? userPf : "%ProgramFiles(x86)%",
            ["autopf64"] = perUser ? userPf : "%ProgramFiles%",
            ["autocf"] = perUser ? userCf : cf,
            ["autocf32"] = perUser ? userCf : "%CommonProgramFiles(x86)%",
            ["autocf64"] = perUser ? userCf : "%CommonProgramFiles%",
            ["pf"] = pf, ["commonpf"] = pf,
            ["pf32"] = "%ProgramFiles(x86)%", ["commonpf32"] = "%ProgramFiles(x86)%",
            ["pf64"] = "%ProgramFiles%", ["commonpf64"] = "%ProgramFiles%",
            ["cf"] = cf, ["commoncf"] = cf,
            ["cf32"] = "%CommonProgramFiles(x86)%", ["commoncf32"] = "%CommonProgramFiles(x86)%",
            ["cf64"] = "%CommonProgramFiles%", ["commoncf64"] = "%CommonProgramFiles%",
            ["userpf"] = userPf, ["usercf"] = userCf, ["usercf64"] = userCf,
            ["localappdata"] = "%LOCALAPPDATA%",
            ["userappdata"] = "%APPDATA%",
            ["commonappdata"] = "%ProgramData%",
            ["autoappdata"] = perUser ? "%APPDATA%" : "%ProgramData%",
            ["userdocs"] = @"%USERPROFILE%\Documents",
            ["commondocs"] = @"%PUBLIC%\Documents",
            ["autodocs"] = perUser ? @"%USERPROFILE%\Documents" : @"%PUBLIC%\Documents",
            ["userdesktop"] = @"%USERPROFILE%\Desktop",
            ["commondesktop"] = @"%PUBLIC%\Desktop",
            ["autodesktop"] = perUser ? @"%USERPROFILE%\Desktop" : @"%PUBLIC%\Desktop",
            ["userprograms"] = @"%APPDATA%\Microsoft\Windows\Start Menu\Programs",
            ["commonprograms"] = @"%ProgramData%\Microsoft\Windows\Start Menu\Programs",
            ["autoprograms"] = perUser ? @"%APPDATA%\Microsoft\Windows\Start Menu\Programs" : @"%ProgramData%\Microsoft\Windows\Start Menu\Programs",
            ["sd"] = "%SystemDrive%",
            ["win"] = "%SystemRoot%",
            ["sys"] = @"%SystemRoot%\System32",
            ["sysnative"] = @"%SystemRoot%\System32",
            ["syswow64"] = @"%SystemRoot%\SysWOW64",
            ["tmp"] = "%TEMP%",
            ["fonts"] = @"%SystemRoot%\Fonts"
        };

        // "{{" is the escape for a literal brace and must survive expansion.
        const char braceMark = (char)1;
        var text = path.Replace("{{", braceMark.ToString());
        if (!string.IsNullOrEmpty(appDir)) text = Regex.Replace(text, @"(?i)\{app\}", appDir.Replace("$", "$$"));
        text = Regex.Replace(text, @"\{%([^}|]+)(?:\|[^}]*)?\}", m => "%" + m.Groups[1].Value + "%");
        text = Regex.Replace(text, @"\{([A-Za-z0-9]+)\}", m => map.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        return text.Replace(braceMark, '{');
    }

    /// <summary>
    /// Extracts AppId, version, install directory, install mode and the
    /// Add/Remove Programs registration from the compiled [Setup] header.
    /// The key is &lt;AppId&gt;_is1 under HKLM (WOW6432Node when the setup does
    /// not install in 64-bit mode) or under HKCU for PrivilegesRequired=lowest.
    /// </summary>
    public static InnoSetupMetadata Read(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var meta = new InnoSetupMetadata();
        if (!File.Exists(path)) { meta.Note = "Installer not found: " + path; return meta; }
        meta.RequestedExecutionLevel = PeResources.ReadRequestedExecutionLevel(path);

        var offsets = ReadLoaderOffsets(path);
        if (offsets is null) { meta.Note = "SetupLdr offset table not found."; return meta; }

        SetupHeader? header = null;
        HeaderBlock? block = null;
        var decodeNote = "";
        foreach (var budget in new[] { 1 << 20, 8 << 20, 48 << 20 })
        {
            ct.ThrowIfCancellationRequested();
            block = ExpandHeaderBlock(path, offsets.HeaderOffset, budget, ct);
            if (block?.Bytes is null) break;
            try
            {
                header = ParseSetupHeader(block.Bytes, block.Version!);
                break;
            }
            catch (EndOfStreamException)
            {
                if (block.Bytes.Length < budget) { decodeNote = "Setup header record ends inside the decoded block."; break; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                decodeNote = "Setup header record could not be decoded.";
                break;
            }
        }
        if (block is not null)
        {
            if (block.Version is not null) meta.DataVersion = block.Version.Text + (block.Version.Unicode ? " (Unicode)" : " (ANSI)");
            meta.Compression = block.Compression;
        }
        if (header is null)
        {
            meta.Note = block is not null && !string.IsNullOrEmpty(block.Note) ? block.Note
                : !string.IsNullOrEmpty(decodeNote) ? decodeNote
                : "Setup header record could not be decoded.";
            return meta;
        }

        meta.HeaderAvailable = true;
        meta.AppId = header.Get("AppId");
        meta.AppName = header.Get("AppName");
        meta.AppVersion = header.Get("AppVersion");
        meta.DisplayName = header.Get("AppName").Trim();
        meta.DisplayVersion = header.Get("AppVersion").Trim();
        meta.Publisher = header.Get("AppPublisher").Trim();
        meta.DefaultDirName = header.Get("DefaultDirName");
        meta.UninstallFilesDir = header.Get("UninstallFilesDir");
        meta.CreateUninstallRegKey = header.Get("CreateUninstallRegKey");
        meta.Uninstallable = header.Get("Uninstallable");
        meta.MinWindowsVersion = header.MinWindowsVersion;
        if (header.ArchitecturesAllowed is not null) meta.ArchitecturesAllowed = header.ArchitecturesAllowed;
        if (header.ArchitecturesInstallIn64BitMode is not null) meta.ArchitecturesInstallIn64BitMode = header.ArchitecturesInstallIn64BitMode;
        if (header.FixedDecoded)
        {
            meta.PrivilegesRequired = header.PrivilegesRequired;
            meta.PrivilegesRequiredOverridesAllowed = [.. header.PrivilegesRequiredOverridesAllowed];
            if (!string.IsNullOrEmpty(header.CompressMethod)) meta.Compression = block!.Compression + " (files: " + header.CompressMethod + ")";
        }
        else if (!string.IsNullOrEmpty(header.FixedNote))
        {
            meta.Note = header.FixedNote;
        }

        // 64-bit install mode on an x64 host: an expression naming x64 (6.3+)
        // or the x64 flag (older). The ARP key and Program Files folder follow it.
        meta.Is64BitInstallMode = Regex.IsMatch(meta.ArchitecturesInstallIn64BitMode, @"(?i)(?<!not\s+)\b(x64compatible|x64os|x64|win64)\b");

        var perUser = meta.PrivilegesRequired == "lowest";
        meta.InstallContext = header.FixedDecoded ? (perUser ? "PerUser" : "PerMachine") : "";
        if (!header.FixedDecoded && Regex.IsMatch(meta.DefaultDirName, @"(?i)^\{(localappdata|userappdata|userpf|usercf|userdocs|userdesktop|userprograms)\}"))
        {
            perUser = true;
            meta.InstallContext = "PerUser";
        }

        meta.InstallDir = meta.DefaultDirName;
        var uninstallDir = !string.IsNullOrEmpty(meta.UninstallFilesDir) ? meta.UninstallFilesDir : "{app}";
        meta.UninstallerPath = uninstallDir.TrimEnd('\\') + @"\unins000.exe";
        var uninstallable = !Regex.IsMatch(meta.Uninstallable, "^(?i)no$");
        if (!uninstallable) meta.UninstallerPath = "";

        // AppId written as "{{GUID}" in the script is stored with the brace
        // escape; the key name carries the literal brace.
        const char braceMark = (char)1;
        var appIdLiteral = meta.AppId.Replace("{{", braceMark.ToString());
        var keyName = "";
        var keyNote = "";
        if (appIdLiteral.Contains('{')) keyNote = "AppId is computed at run time: " + meta.AppId;
        else if (!uninstallable) keyNote = "Uninstallable=no: the setup writes no uninstaller and no Add/Remove Programs entry.";
        else if (Regex.IsMatch(meta.CreateUninstallRegKey, "^(?i)no$")) keyNote = "CreateUninstallRegKey=no: the setup writes no Add/Remove Programs entry.";
        else if (!string.IsNullOrEmpty(appIdLiteral)) keyName = appIdLiteral.Replace(braceMark, '{') + "_is1";
        var overrides = header.FixedDecoded ? meta.PrivilegesRequiredOverridesAllowed : [];
        var overrideNote = overrides.Count > 0
            ? "PrivilegesRequiredOverridesAllowed=" + string.Join(",", overrides) + ": /ALLUSERS and /CURRENTUSER switch the install mode and the hive."
            : "";

        InstallBranch ResolveBranch(bool branchPerUser, string modeSwitch)
        {
            var b = new InstallBranch
            {
                InstallDir = meta.DefaultDirName,
                UninstallerPath = meta.UninstallerPath,
                InstallArgs = (SilentArgs + " /SP- " + modeSwitch).Trim(),
                UninstallRegistryKeyNote = keyNote,
                InstallContext = branchPerUser ? "PerUser" : "PerMachine"
            };
            var dirWindows = ToWindowsPath(meta.DefaultDirName, meta.Is64BitInstallMode, branchPerUser);
            b.InstallDirWindows = dirWindows;
            var dirResolved = !dirWindows.Contains('{') && Regex.IsMatch(dirWindows, @"^(%[^%]+%|[A-Za-z]:\\)");
            b.Resolved = dirResolved;
            if (dirResolved && uninstallable)
            {
                var uninstallerWindows = ToWindowsPath(meta.UninstallerPath, meta.Is64BitInstallMode, branchPerUser, dirWindows);
                if (!uninstallerWindows.Contains('{'))
                {
                    b.UninstallerPathWindows = uninstallerWindows;
                    b.SilentUninstallCommand = "\"" + uninstallerWindows + "\" " + SilentArgs;
                }
            }
            if (!string.IsNullOrEmpty(keyName))
            {
                if (branchPerUser)
                {
                    b.RegistryHive = "HKCU";
                    b.UninstallRegistryKey = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName;
                    b.UninstallRegistryKeyNote = "Per-user registration under HKCU; detection and uninstall must run in the user context.";
                }
                else
                {
                    b.RegistryHive = "HKLM";
                    b.RegistryView = meta.Is64BitInstallMode ? "64" : "32";
                    if (meta.Is64BitInstallMode)
                    {
                        b.UninstallRegistryKey = @"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName;
                    }
                    else
                    {
                        b.UninstallRegistryKey = @"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName;
                        b.UninstallRegistryKeyNote = "Setup does not install in 64-bit mode: the key lands under WOW6432Node on x64 Windows.";
                    }
                }
                if (!string.IsNullOrEmpty(overrideNote)) b.UninstallRegistryKeyNote = (b.UninstallRegistryKeyNote + " " + overrideNote).Trim();
            }
            return b;
        }

        var defaultBranch = ResolveBranch(perUser, "");
        meta.InstallDirWindows = defaultBranch.InstallDirWindows;
        meta.UninstallerPathWindows = defaultBranch.UninstallerPathWindows;
        meta.SilentUninstallCommand = defaultBranch.SilentUninstallCommand;
        meta.UninstallRegistryKey = defaultBranch.UninstallRegistryKey;
        meta.UninstallRegistryKeyNote = defaultBranch.UninstallRegistryKeyNote;
        meta.RegistryHive = defaultBranch.RegistryHive;
        meta.RegistryView = defaultBranch.RegistryView;
        if (!string.IsNullOrEmpty(keyName) && perUser && !string.IsNullOrEmpty(meta.UninstallRegistryKeyNote))
            meta.UninstallRegistryKeyNote = Regex.Replace(meta.UninstallRegistryKeyNote, "^Per-user registration", "PrivilegesRequired=lowest: per-user registration");
        var installDirResolved = defaultBranch.Resolved;

        // /ALLUSERS and /CURRENTUSER are accepted only when the script allows
        // the command-line override; the other branch is then a real option.
        var defaultMode = perUser ? "CurrentUser" : "AllUsers";
        meta.InstallMode = defaultMode;
        var variants = new Dictionary<string, InstallBranch>(StringComparer.OrdinalIgnoreCase) { [defaultMode] = defaultBranch };
        if (overrides.Contains("commandline"))
        {
            meta.AllUsersSwitch = "/ALLUSERS";
            meta.CurrentUserSwitch = "/CURRENTUSER";
            if (perUser) variants["AllUsers"] = ResolveBranch(false, "/ALLUSERS");
            else variants["CurrentUser"] = ResolveBranch(true, "/CURRENTUSER");
        }
        meta.ModeVariants = variants;
        meta.InstallModes = [.. new[] { "CurrentUser", "AllUsers" }.Where(variants.ContainsKey)];

        // Programs and Features shows UninstallDisplayName when set, otherwise AppVerName.
        var arpName = !string.IsNullOrEmpty(header.Get("UninstallDisplayName")) ? header.Get("UninstallDisplayName")
            : !string.IsNullOrEmpty(header.Get("AppVerName")) ? header.Get("AppVerName")
            : (header.Get("AppName") + " version " + header.Get("AppVersion")).Trim();
        meta.ArpDisplayName = arpName;
        var arp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DisplayName"] = arpName };
        if (!string.IsNullOrEmpty(meta.DisplayVersion)) arp["DisplayVersion"] = meta.DisplayVersion;
        if (!string.IsNullOrEmpty(meta.Publisher)) arp["Publisher"] = meta.Publisher;
        if (!string.IsNullOrEmpty(header.Get("AppPublisherURL"))) arp["URLInfoAbout"] = header.Get("AppPublisherURL");
        if (!string.IsNullOrEmpty(header.Get("AppUpdatesURL"))) arp["URLUpdateInfo"] = header.Get("AppUpdatesURL");
        if (!string.IsNullOrEmpty(header.Get("AppSupportURL"))) arp["HelpLink"] = header.Get("AppSupportURL");
        if (installDirResolved) arp["InstallLocation"] = meta.InstallDirWindows.TrimEnd('\\') + "\\";
        if (!string.IsNullOrEmpty(meta.UninstallerPathWindows))
        {
            arp["UninstallString"] = "\"" + meta.UninstallerPathWindows + "\"";
            arp["QuietUninstallString"] = "\"" + meta.UninstallerPathWindows + "\" /SILENT";
        }
        meta.ArpValues = arp;

        if (!installDirResolved && !string.IsNullOrEmpty(meta.DefaultDirName))
            meta.Note = ("Install directory is not resolvable before install: " + meta.DefaultDirName + (string.IsNullOrEmpty(meta.Note) ? "" : " " + meta.Note)).Trim();
        return meta;
    }
}
