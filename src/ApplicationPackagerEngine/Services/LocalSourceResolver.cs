using System.Diagnostics;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Picks the installer a local-source definition stages. Vendors reuse one
/// file name across releases, so the file version decides (the ProductVersion
/// for an MSI file), then the newest write time.
/// </summary>
public static class LocalSourceResolver
{
    public const string SettingsLocation = "Settings > Local Installer Sources";

    /// <summary>
    /// The installer path for <paramref name="owner"/>. Throws when no folder
    /// is set, the folder is missing, or no file matches the filter after the
    /// excludes.
    /// </summary>
    public static string Resolve(string? folder, LocalSourceConfig config, string owner)
    {
        // Every refusal names what the folder must hold, so the operator knows what to put there.
        var what = string.IsNullOrWhiteSpace(config.Note) ? "" : " " + config.Note.Trim();
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException(
                $"No installer source folder is set for {owner}. Choose one in {SettingsLocation}.{what}");
        if (!Directory.Exists(folder))
            throw new InvalidOperationException($"Installer source folder for {owner} not found: {folder}.{what}");

        var candidates = Directory.GetFiles(folder, config.Filter, SearchOption.TopDirectoryOnly)
            .Where(f => !config.Exclude.Any(x => WildcardMatch(Path.GetFileName(f), x)))
            .ToList();
        if (candidates.Count == 0)
        {
            var zipNote = Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly).Length > 0
                ? " The folder holds a .zip file. Unzip it and keep the installer in the folder itself; a .zip is not read."
                : "";
            throw new InvalidOperationException($"No installer matching '{config.Filter}' found in {folder}.{what}{zipNote}");
        }

        return candidates
            .Select(f => (Path: f, Version: RankVersion(f), Written: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.Written)
            .First().Path;
    }

    /// <summary>
    /// The leading one to four numbers of the FileVersion string, which may
    /// carry a build label after them; 0.0 when there are none.
    /// </summary>
    public static Version RankVersion(string path)
    {
        var raw = FileVersionText(path);
        var m = Regex.Match(raw, @"^\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : new Version(0, 0);
    }

    /// <summary>The FileVersion the staged folder and detection carry; throws when it is not numeric.</summary>
    public static string ReadVersion(string path)
    {
        var raw = FileVersionText(path);
        if (!Version.TryParse(raw, out _))
            throw new InvalidOperationException($"Installer carries no numeric file version: {path}");
        return raw;
    }

    /// <summary>
    /// The version text a file carries: the ProductVersion property of an MSI file, the FileVersion of any other file;
    /// empty when it has none.
    /// </summary>
    public static string FileVersionText(string path)
    {
        if (Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            try { return new MsiInspectionService().ReadProperties(path).ProductVersion.Trim(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
        }
        return (FileVersionInfo.GetVersionInfo(path).FileVersion ?? "").Trim();
    }

    /// <summary>The staged name for a source file: the first matching rule, else the fallback.</summary>
    public static string StagedName(LocalSourceConfig config, string sourcePath, string version, string fallback)
    {
        var name = Path.GetFileName(sourcePath);
        var rule = config.StagedNames.FirstOrDefault(r => WildcardMatch(name, r.Match));
        var template = rule?.FileName ?? fallback;
        return template.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PowerShell -like semantics for '*' and '?', without case.</summary>
    public static bool WildcardMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }
}
