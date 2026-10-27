using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ApplicationPackagerEngine.Services;

/// <summary>A Windows Installer handle; MsiCloseHandle runs once when the handle is released.</summary>
internal sealed class MsiHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MsiHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => MsiNative.MsiCloseHandle(handle) == 0;
}

internal static class MsiNative
{
    public const uint Success = 0;
    public const uint NoMoreItems = 259;
    public const uint MoreData = 234;
    public const int NullInteger = int.MinValue;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiOpenDatabaseW(string szDatabasePath, IntPtr szPersist, out MsiHandle phDatabase);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiDatabaseOpenViewW(MsiHandle hDatabase, string szQuery, out MsiHandle phView);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiViewExecute(MsiHandle hView, IntPtr hRecord);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiViewFetch(MsiHandle hView, out MsiHandle phRecord);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiRecordGetStringW(MsiHandle hRecord, uint iField, [Out] char[]? szValueBuf, ref uint pcchValueBuf);

    [DllImport("msi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MsiRecordIsNull(MsiHandle hRecord, uint iField);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern int MsiRecordGetInteger(MsiHandle hRecord, uint iField);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiRecordDataSize(MsiHandle hRecord, uint iField);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiRecordReadStream(MsiHandle hRecord, uint iField, [Out] byte[] szDataBuf, ref uint pcbDataBuf);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int MsiDatabaseIsTablePersistentW(MsiHandle hDatabase, string szTableName);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiGetSummaryInformationW(IntPtr hDatabase, string szDatabasePath, uint uiUpdateCount, out MsiHandle phSummaryInfo);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiSummaryInfoGetPropertyW(MsiHandle hSummaryInfo, uint uiProperty, out uint puiDataType,
        out int piValue, out System.Runtime.InteropServices.ComTypes.FILETIME pftValue, [Out] char[]? szValueBuf, ref uint pcchValueBuf);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiCloseHandle(IntPtr hAny);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiExtractPatchXMLDataW(string szPatchPath, uint dwReserved, [Out] char[]? szXMLData, ref uint pcchXMLData);
}

/// <summary>
/// A read-only Windows Installer database (an .msi, or an .msp opened as a patch file)
/// read through msi.dll. Every handle is a <see cref="MsiHandle"/>, so nothing stays open
/// after <see cref="Dispose"/> and no COM wrapper is left for the finalizer.
/// </summary>
internal sealed class MsiDatabase : IDisposable
{
    // MSIDBOPEN_READONLY is a null pointer; MSIDBOPEN_PATCHFILE adds 32 bytes to it.
    private static readonly IntPtr ReadOnly = IntPtr.Zero;
    private static readonly IntPtr PatchFile = new(32);

    private readonly MsiHandle _handle;

    private MsiDatabase(MsiHandle handle) => _handle = handle;

    /// <summary>Opens the file read-only, or throws <see cref="InvalidDataException"/> with the Windows Installer error code.</summary>
    public static MsiDatabase Open(string path, bool patch = false)
    {
        var error = MsiNative.MsiOpenDatabaseW(path, patch ? PatchFile : ReadOnly, out var handle);
        if (error != MsiNative.Success)
        {
            handle.Dispose();
            throw new InvalidDataException($"The file is not a readable Windows Installer {(patch ? "patch" : "database")} (error {error}).");
        }
        return new MsiDatabase(handle);
    }

    public bool HasTable(string table) => MsiNative.MsiDatabaseIsTablePersistentW(_handle, table) == 1;

    /// <summary>
    /// Runs one query and hands every record to <paramref name="onRecord"/> until it returns
    /// false or <paramref name="maxRows"/> records were read. Returns the number read and
    /// whether the limit stopped the read.
    /// </summary>
    public (int Rows, bool Truncated) Query(string sql, Func<MsiRecord, bool> onRecord, int maxRows, CancellationToken ct)
    {
        var error = MsiNative.MsiDatabaseOpenViewW(_handle, sql, out var view);
        using (view)
        {
            if (error != MsiNative.Success) throw new InvalidDataException($"A table query failed (error {error}).");
            error = MsiNative.MsiViewExecute(view, IntPtr.Zero);
            if (error != MsiNative.Success) throw new InvalidDataException($"A table query failed (error {error}).");
            var rows = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                error = MsiNative.MsiViewFetch(view, out var record);
                using (record)
                {
                    if (error == MsiNative.NoMoreItems) return (rows, false);
                    if (error != MsiNative.Success) throw new InvalidDataException($"A table row could not be read (error {error}).");
                    if (rows >= maxRows) return (rows, true);
                    rows++;
                    if (!onRecord(new MsiRecord(record))) return (rows, false);
                }
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>The largest patch applicability XML read; a patch with many targets and sequences stays far below it.</summary>
    private const uint MaxPatchXmlChars = 1024 * 1024;

    /// <summary>The applicability XML MsiExtractPatchXMLData returns for a patch, or null when it fails.</summary>
    public static string? ReadPatchXml(string path)
    {
        uint length = 0;
        if (MsiNative.MsiExtractPatchXMLDataW(path, 0, null, ref length) != MsiNative.Success || length == 0 || length > MaxPatchXmlChars) return null;
        var buffer = new char[length + 1];
        length++;
        return MsiNative.MsiExtractPatchXMLDataW(path, 0, buffer, ref length) == MsiNative.Success ? new string(buffer, 0, (int)length) : null;
    }

    /// <summary>
    /// The UpdatedVersion of the target product with <paramref name="productCode"/>: its ProductVersion after the patch.
    /// With <paramref name="baseVersion"/>, only the targets whose TargetVersion condition (ComparisonType,
    /// ComparisonFilter, Validate) the base version meets count. The counted targets must all carry the same
    /// UpdatedVersion; null otherwise, because a target with no UpdatedVersion is a small update that keeps the
    /// ProductVersion.
    /// </summary>
    public static string? PatchUpdatedVersion(string? xml, string productCode, string? baseVersion = null)
    {
        if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(productCode)) return null;
        var updated = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPatchXmlChars };
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
            var root = System.Xml.Linq.XDocument.Load(reader).Root;
            foreach (var target in root?.Elements().Where(e => e.Name.LocalName == "TargetProduct") ?? [])
            {
                var children = target.Elements().ToList();
                if (!children.Any(e => e.Name.LocalName == "TargetProductCode" && string.Equals(e.Value.Trim(), productCode, StringComparison.OrdinalIgnoreCase))) continue;
                var targetVersion = children.FirstOrDefault(e => e.Name.LocalName == "TargetVersion");
                if (!string.IsNullOrWhiteSpace(baseVersion) && (targetVersion is null || !MeetsTargetVersion(targetVersion, baseVersion))) continue;
                updated.Add(children.FirstOrDefault(e => e.Name.LocalName == "UpdatedVersion")?.Value.Trim() ?? "");
            }
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        return updated.Count == 1 && updated.First() is var only && System.Text.RegularExpressions.Regex.IsMatch(only, @"^\d{1,5}(\.\d{1,5}){0,3}$") ? only : null;
    }

    /// <summary>
    /// True when <paramref name="baseVersion"/> meets the TargetVersion condition of the patch applicability schema:
    /// Validate false, or ComparisonType or ComparisonFilter None, always meets it; the filter names how many leading
    /// fields count (Major 1, MajorMinor 2, MajorMinorUpdate 3).
    /// </summary>
    private static bool MeetsTargetVersion(System.Xml.Linq.XElement targetVersion, string baseVersion)
    {
        string Attribute(string name, string fallback) => (string?)targetVersion.Attribute(name) ?? fallback;
        if (string.Equals(Attribute("Validate", "true"), "false", StringComparison.OrdinalIgnoreCase)) return true;
        var type = Attribute("ComparisonType", "Equal");
        var filter = Attribute("ComparisonFilter", "MajorMinorUpdate");
        if (type == "None" || filter == "None") return true;
        var count = filter switch { "Major" => 1, "MajorMinor" => 2, _ => 3 };
        if (Fields(baseVersion, count) is not { } current || Fields(targetVersion.Value, count) is not { } wanted) return false;
        var compared = current.Zip(wanted, (a, b) => a.CompareTo(b)).FirstOrDefault(c => c != 0);
        return type switch
        {
            "LessThan" => compared < 0,
            "LessThanOrEqual" => compared <= 0,
            "Equal" => compared == 0,
            "GreaterThanOrEqual" => compared >= 0,
            "GreaterThan" => compared > 0,
            _ => false
        };
    }

    /// <summary>The first <paramref name="count"/> numeric fields of a version; a missing field counts as 0.</summary>
    private static int[]? Fields(string version, int count)
    {
        var parts = version.Trim().Split('.');
        if (parts.Length > 4) return null;
        var fields = new int[count];
        for (var i = 0; i < Math.Min(count, parts.Length); i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out fields[i])) return null;
        return fields;
    }

    /// <summary>The summary information stream: string and integer properties by property id.</summary>
    public static Dictionary<int, string> ReadSummary(string path)
    {
        var result = new Dictionary<int, string>();
        var error = MsiNative.MsiGetSummaryInformationW(IntPtr.Zero, path, 0, out var summary);
        using (summary)
        {
            if (error != MsiNative.Success) return result;
            foreach (var pid in new[] { 2, 3, 4, 5, 6, 7, 9, 12, 13, 14, 15, 18, 19 })
            {
                uint length = 0;
                error = MsiNative.MsiSummaryInfoGetPropertyW(summary, (uint)pid, out var type, out var number, out var time, null, ref length);
                if (error != MsiNative.Success && error != MsiNative.MoreData) continue;
                switch (type)
                {
                    case 30: // VT_LPSTR
                    {
                        if (length > 1_000_000) continue;
                        var buffer = new char[length + 1];
                        length++;
                        if (MsiNative.MsiSummaryInfoGetPropertyW(summary, (uint)pid, out _, out _, out _, buffer, ref length) == MsiNative.Success)
                            result[pid] = new string(buffer, 0, (int)length);
                        break;
                    }
                    case 2: // VT_I2
                    case 3: // VT_I4
                        result[pid] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case 64: // VT_FILETIME
                    {
                        var ticks = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
                        if (ticks > 0 && ticks < DateTime.MaxValue.ToFileTimeUtc())
                            result[pid] = DateTime.FromFileTimeUtc(ticks).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
        }
        return result;
    }
}

/// <summary>One fetched record; valid only inside the query callback.</summary>
internal readonly struct MsiRecord(MsiHandle handle)
{
    /// <summary>The field as text, or null when the field is null. Text longer than <paramref name="maxChars"/> is cut.</summary>
    public string? String(uint field, int maxChars = 1_000_000)
    {
        if (MsiNative.MsiRecordIsNull(handle, field)) return null;
        uint length = 0;
        var error = MsiNative.MsiRecordGetStringW(handle, field, null, ref length);
        if (error != MsiNative.Success && error != MsiNative.MoreData) return null;
        // The buffer is sized from the limit, never from the length the record reports; a longer
        // value comes back cut to the buffer with ERROR_MORE_DATA.
        var capacity = (uint)Math.Max(1, Math.Min((long)length, Math.Max(0, maxChars))) + 1;
        var buffer = new char[capacity];
        var size = capacity;
        error = MsiNative.MsiRecordGetStringW(handle, field, buffer, ref size);
        if (error == MsiNative.Success) return new string(buffer, 0, (int)Math.Min(size, capacity - 1));
        if (error != MsiNative.MoreData) return null;
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? (int)capacity - 1 : end);
    }

    /// <summary>The integer field, or null for a null field.</summary>
    public int? Integer(uint field)
    {
        var value = MsiNative.MsiRecordGetInteger(handle, field);
        return value == MsiNative.NullInteger ? null : value;
    }

    public long DataSize(uint field) => MsiNative.MsiRecordDataSize(handle, field);

    /// <summary>Copies a stream field to <paramref name="target"/>; throws when it is longer than <paramref name="maxBytes"/>.</summary>
    public long CopyStream(uint field, Stream target, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = (uint)buffer.Length;
            var error = MsiNative.MsiRecordReadStream(handle, field, buffer, ref count);
            if (error != MsiNative.Success) throw new InvalidDataException($"A stream could not be read (error {error}).");
            if (count == 0) return total;
            total += count;
            if (total > maxBytes) throw new InstallerAnalysisException("The entry is larger than the extraction limit.");
            target.Write(buffer, 0, (int)count);
        }
    }
}
