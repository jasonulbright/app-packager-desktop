using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// What the One Click plan reads while it is open. The plan window replans on each check box, on the UI thread, so a replan reads the settings
/// file once, and the definition of each application is read once, when the plan is built.
/// </summary>
public sealed class OneClickPlanSources
{
    private readonly Func<AppSettings> _load;
    private readonly bool _consoleFound;
    private readonly Dictionary<string, string?> _ids = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings? _settings;

    public OneClickPlanSources(Func<AppSettings> load, bool consoleFound, IEnumerable<PackagerInfo> rows, Func<PackagerInfo, string?> applicationId)
    {
        _load = load;
        _consoleFound = consoleFound;
        foreach (var row in rows.Where(Decides)) _ids[row.Uid] = applicationId(row);
    }

    // A PS1 packager decides by its own script, so the plan says nothing about it.
    private static bool Decides(PackagerInfo row) => row.IsJsonDefined || row.StagedManifestPath is not null;

    /// <summary>Reads the saved settings. A replan calls this once, before it asks about supersedence.</summary>
    public OneClickPlanSettings PlanSettings() => OneClickSettingsMap.PlanSettings(_settings = _load(), _consoleFound);

    /// <summary>True when the effective supersedence mode of the application is on, by the settings of the last read.</summary>
    public bool Supersedes(PackagerInfo row) => Decides(row)
        && SupersedenceSettingsEditor.EffectiveMode((_settings ??= _load()).Supersedence ?? new(), _ids.GetValueOrDefault(row.Uid)) != SupersedenceMode.Off;
}
