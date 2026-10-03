using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Which One Click rows Stage and Package goes on to publish. A row staged only because
/// its last sealed build is stale, or because no build was ever sealed, already has its
/// vendor version in ConfigMgr: it is restaged so the build record exists, and is not
/// published again, so no existing-application prompt is raised for it.
/// </summary>
public static class OneClickPublishSelection
{
    public static IEnumerable<PackagerInfo> Publishable(IEnumerable<PackagerInfo> staged, ISet<PackagerInfo> versionUpdates) =>
        staged.Where(p => p.Status == "Staged" && versionUpdates.Contains(p));
}
