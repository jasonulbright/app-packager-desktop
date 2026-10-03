using System.Diagnostics;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Picks the installer a local-source definition stages. Vendors reuse one
/// file name across releases, so the file version decides, then the newest
/// write time.
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
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException(
                $"No installer source folder is set for {owner}. Choose one in {SettingsLocation}.");
        if (!Directory.Exists(folder))
            throw new InvalidOperationException($"Installer source folder for {owner} not found: {folder}");

        var candidates = Directory.GetFiles(folder, config.Filter, SearchOption.TopDirectoryOnly)
            .Where(f => !config.Exclude.Any(x => WildcardMatch(Path.GetFileName(f), x)))
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No installer matching '{config.Filter}' found in {folder}");

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
        var raw = (FileVersionInfo.GetVersionInfo(path).FileVersion ?? "").Trim();
        var m = Regex.Match(raw, @"^\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : new Version(0, 0);
    }

    /// <summary>The FileVersion the staged folder and detection carry; throws when it is not numeric.</summary>
    public static string ReadVersion(string path)
    {
        var raw = (FileVersionInfo.GetVersionInfo(path).FileVersion ?? "").Trim();
        if (!Version.TryParse(raw, out _))
            throw new InvalidOperationException($"Installer carries no numeric file version: {path}");
        return raw;
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
