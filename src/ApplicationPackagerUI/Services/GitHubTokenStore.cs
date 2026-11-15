using System.Security.Cryptography;
using System.Text;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The GitHub token that the operator saves in Settings: protected with DPAPI for the current Windows user, so the plaintext never reaches
/// settings.json. The environment variables and the gh CLI still work; the saved token comes after the variables.
/// </summary>
public static class GitHubTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ApplicationPackager.GitHub.Token");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { Array.Clear(bytes); }
    }

    /// <summary>The plaintext token, or an empty string when none is stored or it cannot be recovered.</summary>
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
}