using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Before a Stage, Package or One Click run, asks for the installer folder of every
/// selected application whose definition requires a local source and has no folder that
/// exists. A chosen folder is stored; a cancelled prompt drops that application from the run.
/// </summary>
public static class LocalSourceFolderPrompt
{
    public static string Description(string application) =>
        $"Choose the folder that holds the {application} installer. Its download requires a sign-in, so it stages from a local copy.";

    /// <param name="rows">The selected rows in run order.</param>
    /// <param name="definitionOf">The row's definition, or null for a row with none.</param>
    /// <param name="pickFolder">Shows the folder prompt with the given description; null when cancelled.</param>
    /// <param name="changed">True when a folder was stored in <paramref name="settings"/>.</param>
    /// <returns>The rows the run keeps, in order.</returns>
    public static List<PackagerInfo> Confirm(
        IEnumerable<PackagerInfo> rows, Func<PackagerInfo, AppDefinition?> definitionOf, AppSettings settings,
        Func<string, string?> pickFolder, Action<string> log, out bool changed)
    {
        changed = false;
        var kept = new List<PackagerInfo>();
        foreach (var row in rows)
        {
            var def = definitionOf(row);
            if (def?.LocalSource is not { IsRequired: true })
            {
                kept.Add(row);
                continue;
            }
            if (settings.LocalSourceFolders.TryGetValue(def.Id, out var folder)
                && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                kept.Add(row);
                continue;
            }

            var chosen = pickFolder(Description(row.Application));
            if (string.IsNullOrWhiteSpace(chosen))
            {
                log($"Skipped {row.Application}: no installer source folder chosen.");
                continue;
            }
            settings.LocalSourceFolders[def.Id] = chosen;
            changed = true;
            log($"Installer source for {row.Application}: {chosen}");
            kept.Add(row);
        }
        return kept;
    }
}
