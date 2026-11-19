using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Moves every setting that names a removed definition to its replacement, so that a hidden, tracked or overridden application
/// keeps its setting and does not vanish (see <see cref="RemovedDefinitions"/>).
/// </summary>
public static class RemovedDefinitionSettings
{
    /// <summary>Applies the move to the settings in memory; returns the notice for the operator, or null when no setting named a removed definition.</summary>
    public static string? Apply(AppSettings settings)
    {
        var used = new List<RemovedDefinitions.Entry>();
        settings.HiddenApplications = RemovedDefinitions.ReplaceIds(settings.HiddenApplications, used);
        settings.DiscoveryDismissed = RemovedDefinitions.ReplaceIds(settings.DiscoveryDismissed, used);
        settings.DiscoveryEnabled = RemovedDefinitions.ReplaceIds(settings.DiscoveryEnabled, used);
        if (settings.OneClick is not null)
        {
            settings.OneClick.TrackedApplications = RemovedDefinitions.ReplaceIds(settings.OneClick.TrackedApplications, used);
            settings.OneClick.CadenceOverrides = RemovedDefinitions.ReplaceKeys(settings.OneClick.CadenceOverrides, used);
        }
        settings.CommandOverrides = RemovedDefinitions.ReplaceKeys(settings.CommandOverrides, used);
        settings.AppStates = RemovedDefinitions.ReplaceKeys(settings.AppStates, used);
        return used.Count == 0 ? null : RemovedDefinitions.NoticeFor(used);
    }
}
