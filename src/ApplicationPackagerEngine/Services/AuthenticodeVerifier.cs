using System.Runtime.InteropServices;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Embedded Authenticode verification through WinVerifyTrust. A file signed
/// only through a system catalog carries no embedded signature and reports
/// NotSigned here.
/// </summary>
public static class AuthenticodeVerifier
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>Returns 0 when the Authenticode signature verifies, otherwise the WinVerifyTrust status.</summary>
    public static int Verify(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(filePath),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };
        var pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var pGuid = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            Marshal.StructureToPtr(GenericVerifyV2, pGuid, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = 0x00000010, // WTD_REVOCATION_CHECK_NONE: the chain is built without revocation checks
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero
            };
            return WinVerifyTrust(IntPtr.Zero, pGuid, ref data);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(pFile);
            Marshal.FreeCoTaskMem(pGuid);
        }
    }

    /// <summary>The Get-AuthenticodeSignature status name for a WinVerifyTrust result.</summary>
    public static string StatusText(int status) => status switch
    {
        0 => "Valid",
        TrustENoSignature => "NotSigned",
        TrustEBadDigest => "HashMismatch",
        TrustEExplicitDistrust or CertEUntrustedRoot or CertEExpired => "NotTrusted",
        _ => $"UnknownError (0x{status:X8})"
    };

    /// <summary>Throws unless the installer carries a valid embedded signature.</summary>
    public static void RequireValid(string filePath, Action<string>? onProgress = null)
    {
        var status = Verify(filePath);
        if (status != 0)
            throw new InvalidOperationException($"Installer signature is {StatusText(status)}, not Valid: {filePath}");
        onProgress?.Invoke($"Installer signature          : Valid ({Path.GetFileName(filePath)})");
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, IntPtr pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
