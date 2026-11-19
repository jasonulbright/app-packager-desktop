using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The supersedence setting of an application: one default in Options, one optional override for each application, and the bulk choices
/// all applications, the checked applications and no applications. Nothing here runs by itself; each call is the choice of the person
/// who picked the menu item, and the log says what it changed.
/// </summary>
public partial class PackagesViewModel
{
    /// <summary>Turns the supersedence of the checked applications on (Replace or Replace and uninstall), off, or back to the default.</summary>
    public void SetSupersedenceForChecked(SupersedenceMode? mode)
    {
        if (IsOperationRunning) { AddLogLine("Supersedence setting skipped: another operation is running."); return; }
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Supersedence"); return; }
        var settings = _settingsService.Load();
        settings.Supersedence ??= new();
        var changed = 0;
        foreach (var p in selected)
        {
            var id = _executionService.SupersedenceApplicationId(p);
            if (id is null) { AddLogLine($"Supersedence: {p.Application} has no application identifier (it is not a catalog or custom definition), so it follows the default."); continue; }
            if (mode is null) SupersedenceSettingsEditor.ClearApplication(settings.Supersedence, id);
            else SupersedenceSettingsEditor.SetApplication(settings.Supersedence, id, mode.Value);
            changed++;
        }
        _settingsService.Save(settings);
        var words = mode is null ? "now follow the default" : $"now have supersedence {SupersedenceNames.Text(mode.Value)}";
        StatusText = $"Supersedence: {changed} of {selected.Count} checked applications {words}.";
        AddLogLine(StatusText + (mode is { } m && m != SupersedenceMode.Off ? " The version is in their titles from the next package run." : ""));
    }

    /// <summary>All applications (the default takes the mode and the single overrides are cleared) or no applications (Off).</summary>
    public void SetSupersedenceForAll(SupersedenceMode mode)
    {
        if (IsOperationRunning) { AddLogLine("Supersedence setting skipped: another operation is running."); return; }
        var settings = _settingsService.Load();
        settings.Supersedence ??= new();
        SupersedenceSettingsEditor.SetAll(settings.Supersedence, mode);
        _settingsService.Save(settings);
        StatusText = mode == SupersedenceMode.Off
            ? "Supersedence is off for all applications."
            : $"Supersedence {SupersedenceNames.Text(mode)} is the default for all applications.";
        AddLogLine(StatusText);
    }

    /// <summary>The supersedence mode of one application, for the grid and the tooltip: its override, else the default.</summary>
    public SupersedenceMode SupersedenceModeOf(PackagerInfo p)
    {
        var settings = _settingsService.Load().Supersedence ?? new();
        return SupersedenceSettingsEditor.EffectiveMode(settings, _executionService.SupersedenceApplicationId(p));
    }
}
