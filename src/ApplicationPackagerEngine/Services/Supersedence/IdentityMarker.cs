using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// The identity record that a ConfigMgr publish writes into the keywords of the application (when it has none of its own), so that a later release can find the
/// older applications of one product line: the title identity (the definition identifier), the profile and the architecture. The
/// base title is never an identity.
/// </summary>
public static class IdentityMarker
{
    public const string Prefix = "APD-Line:";

    /// <summary>The marker of a manifest; null for a title that has no definition identifier (it has no line name yet).</summary>
    public static string? For(StageManifest manifest, string? fallbackApplicationId = null)
    {
        var applicationId = string.IsNullOrWhiteSpace(manifest.ApplicationId) ? fallbackApplicationId : manifest.ApplicationId;
        if (string.IsNullOrWhiteSpace(applicationId)) return null;
        var profile = string.IsNullOrWhiteSpace(manifest.ProfileId) ? "default" : manifest.ProfileId.Trim();
        var architecture = string.IsNullOrWhiteSpace(manifest.Architecture) ? InstallerArchitectures.X64 : manifest.Architecture.Trim();
        return $"{Prefix}{applicationId.Trim()}|{profile}|{architecture}";
    }

    public static bool IsMarker(string keyword) => keyword.StartsWith(Prefix, StringComparison.Ordinal);

}
