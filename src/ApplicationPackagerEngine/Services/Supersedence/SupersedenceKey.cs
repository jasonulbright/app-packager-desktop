using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// What finds the older version of an application on the site: the title without the version, and the publisher. It is the same
/// question that Check ConfigMgr asks (the title rule of <see cref="MecmApplicationTitleRule"/>); nothing is written into the
/// application to make it findable.
/// </summary>
public sealed record SupersedenceKey(string BaseTitle, string Publisher)
{
    /// <summary>The key of a manifest: the application name with this release's version taken out. Null when no title is left.</summary>
    public static SupersedenceKey? For(StageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.AppName)) return null;
        string baseTitle;
        try { baseTitle = ApplicationTitle.Resolve(manifest.AppName, manifest.SoftwareVersion, TitleMode.NoVersion); }
        catch (InvalidOperationException) { return null; }
        return string.IsNullOrWhiteSpace(baseTitle) ? null : new SupersedenceKey(baseTitle.Trim(), (manifest.Publisher ?? "").Trim());
    }
}
