using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// What finds the older version of an application on the site: the title without the version, the publisher and the architecture of the
/// payload. It is the same question that Check ConfigMgr asks (the title rule of <see cref="MecmApplicationTitleRule"/>), then the vendor
/// and the architecture are compared in <see cref="SupersedenceLookup.Choose"/>; nothing is written into the application to make it
/// findable. <see cref="Architecture"/> is x64, x86 or arm64, or empty when the manifest names none.
/// </summary>
public sealed record SupersedenceKey(string BaseTitle, string Publisher, string Architecture = "")
{
    /// <summary>The key of a manifest: the application name with this release's version taken out. Null when no title is left.</summary>
    public static SupersedenceKey? For(StageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.AppName)) return null;
        string baseTitle;
        try { baseTitle = ApplicationTitle.Resolve(manifest.AppName, manifest.SoftwareVersion, TitleMode.NoVersion); }
        catch (InvalidOperationException) { return null; }
        return string.IsNullOrWhiteSpace(baseTitle) ? null
            : new SupersedenceKey(baseTitle.Trim(), (manifest.Publisher ?? "").Trim(), TitleArchitecture.Normalize(manifest.Architecture) ?? "");
    }
}
