using System.Security.Cryptography;
using System.Text;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Protects the Intune client secret at rest with DPAPI scoped to the current Windows user.
/// The stored form is base64 ciphertext; plaintext exists only for the duration of a publish.
/// A blob written by a different user or on a different machine fails to unprotect and is
/// reported as absent rather than throwing.
/// </summary>
public static class IntuneSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ApplicationPackager.Intune.ClientSecret");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>Returns the plaintext secret, or an empty string when it cannot be recovered.</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return "";
        try
        {
            var cipher = Convert.FromBase64String(protectedBase64);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (FormatException)
        {
            return "";
        }
        catch (CryptographicException)
        {
            return "";
        }
    }
}
