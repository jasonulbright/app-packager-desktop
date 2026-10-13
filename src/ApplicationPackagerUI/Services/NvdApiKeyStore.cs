using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Protects the NVD API key at rest with DPAPI scoped to the current Windows user.
/// A blob written by another user or on another machine does not unprotect and reads
/// as no key.
/// </summary>
public static class NvdApiKeyStore
{
    public const string EnvironmentVariable = "NVD_API_KEY";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ApplicationPackager.Vulnerability.NvdApiKey");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { Array.Clear(bytes); }
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return "";
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain).Trim(); }
            finally { Array.Clear(plain); }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return ""; }
    }

    /// <summary>The stored key, else the NVD_API_KEY environment variable, else empty.</summary>
    public static string Resolve(AppSettings settings)
    {
        var key = Unprotect(settings.Vulnerability?.NvdApiKeyProtected);
        return key.Length > 0 ? key : (Environment.GetEnvironmentVariable(EnvironmentVariable) ?? "").Trim();
    }

    public static int CacheTtlMinutes(AppSettings settings) =>
        settings.Vulnerability is { CacheTtlMinutes: >= 0 } v ? v.CacheTtlMinutes : VulnerabilitySettings.DefaultCacheTtlMinutes;
}
