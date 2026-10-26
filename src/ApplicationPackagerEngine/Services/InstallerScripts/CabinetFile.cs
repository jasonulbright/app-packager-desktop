using System.Runtime.InteropServices;

namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// Extracts one named file from a cabinet with SetupIterateCabinetW. Every other file is skipped,
/// a file over the size limit is skipped, and a cabinet that continues in another cabinet stops
/// the iteration.
/// </summary>
internal static class CabinetFile
{
    private const uint CabinetInfo = 0x10;
    private const uint FileInCabinet = 0x11;
    private const uint NeedNewCabinet = 0x12;
    private const uint FileExtracted = 0x13;
    private const uint FileOpAbort = 0;
    private const uint FileOpDoIt = 1;
    private const uint FileOpSkip = 2;
    private const uint ErrorFileNotFound = 2;
    private const int MaxPath = 260;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint FileCallback(IntPtr context, uint notification, UIntPtr param1, UIntPtr param2);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupIterateCabinetW(string cabinetFile, uint reserved, FileCallback callback, IntPtr context);

    /// <summary>True when <paramref name="name"/> was found within the size limit and written to <paramref name="target"/>.</summary>
    public static bool ExtractOne(string cabinet, string name, string target, long maxBytes)
    {
        if (target.Length >= MaxPath) return false;
        var extracted = false;
        var done = false;
        // FILE_IN_CABINET_INFO_W: NameInCabinet (pointer), FileSize, Win32Error, DosDate, DosTime, DosAttribs, FullTargetName[MAX_PATH].
        var sizeOffset = IntPtr.Size;
        var targetOffset = IntPtr.Size + 4 + 4 + 2 + 2 + 2;
        uint Callback(IntPtr context, uint notification, UIntPtr param1, UIntPtr param2)
        {
            switch (notification)
            {
                case FileInCabinet:
                {
                    var info = (IntPtr)(long)param1.ToUInt64();
                    if (done) return FileOpAbort;
                    var inCabinet = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info)) ?? "";
                    var size = (uint)Marshal.ReadInt32(info, sizeOffset);
                    if (!string.Equals(inCabinet, name, StringComparison.Ordinal) || size > maxBytes) return FileOpSkip;
                    var chars = (target + "\0").ToCharArray();
                    Marshal.Copy(chars, 0, info + targetOffset, chars.Length);
                    done = true;
                    return FileOpDoIt;
                }
                case FileExtracted:
                    extracted = true;
                    return 0;
                case NeedNewCabinet:
                    return ErrorFileNotFound;
                case CabinetInfo:
                    return 0;
                default:
                    return 0;
            }
        }
        FileCallback callback = Callback;
        try
        {
            SetupIterateCabinetW(cabinet, 0, callback, IntPtr.Zero);
        }
        finally
        {
            GC.KeepAlive(callback);
        }
        return extracted && File.Exists(target) && new FileInfo(target).Length <= maxBytes;
    }
}
