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
