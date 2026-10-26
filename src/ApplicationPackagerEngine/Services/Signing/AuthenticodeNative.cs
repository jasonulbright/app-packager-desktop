using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>
/// The operating system's Authenticode signer and verifier. Signing goes
/// through CryptUIWizDigitalSign with CRYPTUI_WIZ_NO_UI on a file subject, the
/// call Set-AuthenticodeSignature makes: the subject interface package
/// registered for the file's extension writes the signature, so a .ps1 gets
/// the PowerShell "# SIG #" block. Verification is WinVerifyTrust with the
/// generic verify action and state data, which yields the signer and the
/// timestamper certificates the way Get-AuthenticodeSignature reads them.
/// </summary>
internal static class AuthenticodeNative
{
    private const uint CRYPTUI_WIZ_NO_UI = 0x0001;
    private const uint CRYPTUI_WIZ_DIGITAL_SIGN_SUBJECT_FILE = 0x01;
    private const uint CRYPTUI_WIZ_DIGITAL_SIGN_CERT = 0x01;
    // Set-AuthenticodeSignature's default -IncludeChain is NotRoot.
    private const uint CRYPTUI_WIZ_DIGITAL_SIGN_ADD_CHAIN_NO_ROOT = 2;
    private const uint CRYPT_OID_INFO_NAME_KEY = 2;

    public const uint NTE_BAD_ALGID = 0x80090008;
    public const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    public const uint TRUST_E_BAD_DIGEST = 0x80096010;
    public const uint CRYPT_E_BAD_MSG = 0x8009200D;
    public const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    public const uint TRUST_E_EXPLICIT_DISTRUST = 0x800B0111;

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>
    /// The OID of a hash algorithm name (SHA256, SHA384, ...), or null when
    /// the name is unknown to CryptoAPI.
    /// </summary>
    public static string? FindHashOid(string hashAlgorithm)
    {
        var name = Marshal.StringToHGlobalUni(hashAlgorithm);
        try
        {
            var info = CryptFindOIDInfo(CRYPT_OID_INFO_NAME_KEY, name, 0);
            if (info == IntPtr.Zero) return null;
            // CRYPT_OID_INFO: DWORD cbSize, then the LPCSTR pszOID at pointer alignment.
            return Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(info, IntPtr.Size));
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>
    /// Signs a file in place. Returns 0 on success or the GetLastError value.
    /// 0x80004005, 0x80070001 and 0x80072EE7 come back from a signing that
    /// completed (the last one for an unreachable timestamp server) and are
    /// reported as 0; the caller decides from the verification that follows.
    /// </summary>
    public static uint SignFile(string path, X509Certificate2 certificate, string? timestampUrl, string? hashOid)
    {
        var pFile = Marshal.StringToHGlobalUni(path);
        var pTimestamp = string.IsNullOrEmpty(timestampUrl) ? IntPtr.Zero : Marshal.StringToHGlobalUni(timestampUrl);
        var pDescription = Marshal.StringToHGlobalUni(string.Empty);
        var pMoreInfo = Marshal.StringToHGlobalUni(string.Empty);
        var pHashOid = hashOid is null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(hashOid);
        var pExt = IntPtr.Zero;
        var pInfo = IntPtr.Zero;
        try
        {
            var ext = new CRYPTUI_WIZ_DIGITAL_SIGN_EXTENDED_INFO
            {
                dwSize = (uint)Marshal.SizeOf<CRYPTUI_WIZ_DIGITAL_SIGN_EXTENDED_INFO>(),
                pwszDescription = pDescription,
                pwszMoreInfoLocation = pMoreInfo,
                pszHashAlg = pHashOid
            };
            pExt = Marshal.AllocHGlobal(Marshal.SizeOf<CRYPTUI_WIZ_DIGITAL_SIGN_EXTENDED_INFO>());
            Marshal.StructureToPtr(ext, pExt, false);

            var info = new CRYPTUI_WIZ_DIGITAL_SIGN_INFO
            {
                dwSize = (uint)Marshal.SizeOf<CRYPTUI_WIZ_DIGITAL_SIGN_INFO>(),
                dwSubjectChoice = CRYPTUI_WIZ_DIGITAL_SIGN_SUBJECT_FILE,
                pwszFileName = pFile,
                dwSigningCertChoice = CRYPTUI_WIZ_DIGITAL_SIGN_CERT,
                pSigningCertContext = certificate.Handle,
                pwszTimestampURL = pTimestamp,
                dwAdditionalCertChoice = CRYPTUI_WIZ_DIGITAL_SIGN_ADD_CHAIN_NO_ROOT,
                pSignExtInfo = pExt
            };
            pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<CRYPTUI_WIZ_DIGITAL_SIGN_INFO>());
            Marshal.StructureToPtr(info, pInfo, false);

            var ok = CryptUIWizDigitalSign(CRYPTUI_WIZ_NO_UI, IntPtr.Zero, IntPtr.Zero, pInfo, IntPtr.Zero);
            GC.KeepAlive(certificate);
            if (ok) return 0;
            var error = unchecked((uint)Marshal.GetLastWin32Error());
            return error is 0x80004005 or 0x80070001 or 0x80072EE7 ? 0 : error;
        }
        finally
        {
            if (pInfo != IntPtr.Zero) Marshal.FreeHGlobal(pInfo);
            if (pExt != IntPtr.Zero) Marshal.FreeHGlobal(pExt);
            Marshal.FreeHGlobal(pFile);
            if (pTimestamp != IntPtr.Zero) Marshal.FreeHGlobal(pTimestamp);
            Marshal.FreeHGlobal(pDescription);
            Marshal.FreeHGlobal(pMoreInfo);
            if (pHashOid != IntPtr.Zero) Marshal.FreeHGlobal(pHashOid);
        }
    }

    /// <summary>
    /// WinVerifyTrust over a file with no revocation check and no UI. Returns
    /// the trust result and, when the provider built one, the signer and the
    /// timestamper certificates.
    /// </summary>
    public static (uint Error, X509Certificate2? Signer, X509Certificate2? Timestamper) VerifyFile(string path, bool cacheOnly = false)
    {
        var pPath = Marshal.StringToHGlobalUni(path);
        var pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        X509Certificate2? signer = null;
        X509Certificate2? timestamper = null;
        try
        {
            Marshal.StructureToPtr(new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pPath
            }, pFileInfo, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = pFileInfo,
                dwStateAction = 1,        // WTD_STATEACTION_VERIFY
                // WTD_CACHE_ONLY_URL_RETRIEVAL: chain building uses only cached certificates, never a network fetch.
                dwProvFlags = cacheOnly ? 0x1000u : 0u
            };
            var action = WintrustActionGenericVerifyV2;
            var error = unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref action, ref data));
            try
            {
                if (data.hWVTStateData != IntPtr.Zero)
                {
                    var provData = WTHelperProvDataFromStateData(data.hWVTStateData);
                    var pSigner = provData == IntPtr.Zero ? IntPtr.Zero
                        : WTHelperGetProvSignerFromChain(provData, 0, false, 0);
                    if (pSigner != IntPtr.Zero)
                    {
                        signer = CertificateFromChain(pSigner);
                        var sgnr = Marshal.PtrToStructure<CRYPT_PROVIDER_SGNR>(pSigner);
                        if (sgnr.csCounterSigners == 1 && sgnr.pasCounterSigners != IntPtr.Zero)
                            timestamper = CertificateFromChain(sgnr.pasCounterSigners);
                    }
                }
            }
            finally
            {
                data.dwStateAction = 2;   // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            return (error, signer, timestamper);
        }
        finally
        {
            Marshal.FreeHGlobal(pFileInfo);
            Marshal.FreeHGlobal(pPath);
        }
    }

    private static X509Certificate2? CertificateFromChain(IntPtr pSigner)
    {
        var pProvCert = WTHelperGetProvCertFromChain(pSigner, 0);
        if (pProvCert == IntPtr.Zero) return null;
        // CRYPT_PROVIDER_CERT: DWORD cbStruct, then PCCERT_CONTEXT pCert at pointer alignment.
        var pCert = Marshal.ReadIntPtr(pProvCert, IntPtr.Size);
        return pCert == IntPtr.Zero ? null : new X509Certificate2(pCert);
    }

    [DllImport("cryptui.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUIWizDigitalSign(uint dwFlags, IntPtr hwndParent, IntPtr pwszWizardTitle,
        IntPtr pDigitalSignInfo, IntPtr ppSignContext);

    [DllImport("crypt32.dll")]
    private static extern IntPtr CryptFindOIDInfo(uint dwKeyType, IntPtr pvKey, uint dwGroupId);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner,
        [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPTUI_WIZ_DIGITAL_SIGN_INFO
    {
        public uint dwSize;
        public uint dwSubjectChoice;
        public IntPtr pwszFileName;
        public uint dwSigningCertChoice;
        public IntPtr pSigningCertContext;
        public IntPtr pwszTimestampURL;
        public uint dwAdditionalCertChoice;
        public IntPtr pSignExtInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPTUI_WIZ_DIGITAL_SIGN_EXTENDED_INFO
    {
        public uint dwSize;
        public uint dwAttrFlags;
        public IntPtr pwszDescription;
        public IntPtr pwszMoreInfoLocation;
        public IntPtr pszHashAlg;
        public IntPtr pwszSigningCertDisplayString;
        public IntPtr hAdditionalCertStore;
        public IntPtr psAuthenticated;
        public IntPtr psUnauthenticated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_PROVIDER_SGNR
    {
        public uint cbStruct;
        public System.Runtime.InteropServices.ComTypes.FILETIME sftVerifyAsOf;
        public uint csCertChain;
        public IntPtr pasCertChain;
        public uint dwSignerType;
        public IntPtr psSigner;
        public uint dwError;
        public uint csCounterSigners;
        public IntPtr pasCounterSigners;
        public IntPtr pChainContext;
    }
}
