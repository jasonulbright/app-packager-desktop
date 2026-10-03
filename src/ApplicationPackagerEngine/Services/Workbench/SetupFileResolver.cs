using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>Resolves the entry file an Intune package runs for a staged build.</summary>
public static partial class SetupFileResolver
{
    public const string DefaultSetupFile = "install.bat";

    private static readonly string[] Fallbacks = ["Deploy-Application.exe", "Deploy-Application.ps1", DefaultSetupFile];

    [GeneratedRegex("\"([^\"]+)\"|(\\S+)")]
    private static partial Regex CommandToken();

    [GeneratedRegex(@"(?i)\.(bat|cmd|exe|ps1|msi)$")]
    private static partial Regex EntryExtension();

    /// <summary>
    /// The install command's own first .bat/.cmd/.exe/.ps1/.msi reference that exists
    /// wins, so an edited wrapper or a toolkit layout reports its real entry; then
    /// Deploy-Application.exe, Deploy-Application.ps1 and install.bat. A
    /// DeploymentTypes manifest resolves against its first entry's command and
    /// content subfolder. The result is relative to the stage root.
    /// </summary>
    public static string Resolve(string stageRoot, StageManifest manifest)
    {
        var subRoot = stageRoot;
        var command = string.IsNullOrWhiteSpace(manifest.CommandOverrides?.Install)
            ? DeploymentTypeSpecResolver.DefaultInstallCommand
            : manifest.CommandOverrides!.Install!;
        var first = manifest.DeploymentTypes?.FirstOrDefault();
        if (first is not null)
        {
            if (!string.IsNullOrWhiteSpace(first.ContentSubpath)) subRoot = Path.Combine(stageRoot, first.ContentSubpath);
            if (!string.IsNullOrWhiteSpace(first.InstallCommandLine)) command = first.InstallCommandLine!;
        }

        var candidates = new List<string>();
        foreach (Match match in CommandToken().Matches(command))
        {
            var token = match.Groups[1].Value;
            if (string.IsNullOrEmpty(token)) token = match.Groups[2].Value;
            token = token.Trim();
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-') || token.StartsWith('/')) continue;
            token = Regex.Replace(token, @"^\.\\", "");
            token = Regex.Replace(token, "(?i)^%~dp0", "");
            if (EntryExtension().IsMatch(token)) candidates.Add(token);
        }
        candidates.AddRange(Fallbacks);

        var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
        foreach (var candidate in candidates)
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(subRoot, candidate)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(full)) return full[(rootFull.Length + 1)..];
        }
        return DefaultSetupFile;
    }
}
