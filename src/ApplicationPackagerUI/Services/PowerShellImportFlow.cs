using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>What the operator chose in the preview window.</summary>
public sealed record PowerShellImportChoice(bool Accepted, bool ChooseAnotherFile, IReadOnlySet<string> Keys);

/// <summary>The file picker and the preview window of the import from the PowerShell app.</summary>
public interface IPowerShellImportPrompts
{
    /// <summary>The preferences file to read, or null on cancel.</summary>
    string? PickFile(string? currentPath);

    /// <summary>Shows the preview. Nothing is written before the operator accepts it.</summary>
    PowerShellImportChoice ShowPreview(PowerShellImportPreview preview);
}

/// <summary>The result of one run of the import.</summary>
public sealed record PowerShellImportResult(bool Written, int Count, string Message, string? SourcePath);

/// <summary>
/// The Settings action "Import from App Packager (PowerShell)": find or pick the preferences file, show the preview, and only after the operator accepts it
/// write the rows that differ to the saved settings. The PowerShell file is never changed.
/// </summary>
public sealed class PowerShellImportFlow(SettingsService settingsService, IPowerShellImportPrompts prompts, Func<IEnumerable<string?>> packagersFolders)
{
    public PowerShellImportResult Run()
    {
        var saved = settingsService.Load();
        var path = PowerShellOptionsImport.FindBesideScript(packagersFolders()) ?? prompts.PickFile(null);
        while (path is not null)
        {
            var preview = PowerShellOptionsImport.PreviewFile(path, saved);
            var choice = prompts.ShowPreview(preview);
            if (choice.ChooseAnotherFile)
            {
                path = prompts.PickFile(path);
                continue;
            }

            if (!choice.Accepted || preview.Error is not null) return new PowerShellImportResult(false, 0, "Nothing was imported.", path);
            var count = PowerShellOptionsImport.Apply(preview, saved, choice.Keys);
            if (count == 0) return new PowerShellImportResult(false, 0, "No setting differed, so nothing changed.", path);
            settingsService.SaveOperatorEdit(saved);
            return new PowerShellImportResult(true, count, $"Imported {count} setting{(count == 1 ? "" : "s")} from {System.IO.Path.GetFileName(path)}.", path);
        }

        return new PowerShellImportResult(false, 0, "Nothing was imported.", null);
    }
}
