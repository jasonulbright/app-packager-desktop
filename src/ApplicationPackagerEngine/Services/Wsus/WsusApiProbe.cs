using System.Diagnostics;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// Whether this computer has the WSUS administration API, read from the
/// places the adapter loads it from: the global assembly cache, then the
/// Update Services Api folder. No PowerShell child starts, so the check can
/// gate a button before a run.
/// </summary>
public static class WsusApiProbe
{
    public const string AssemblyFileName = "Microsoft.UpdateServices.Administration.dll";

    /// <summary>The adapter's own ApiMissing text, with the install hint.</summary>
    public const string NotInstalledMessage =
        "The WSUS administration API is not installed on this computer. Install the WSUS console: on Windows 10 or 11, " +
        "Settings > System > Optional features > RSAT: Windows Server Update Services Tools (Add-WindowsCapability -Online -Name Rsat.WSUS.Tools~~~~0.0.1.0); " +
        "on Windows Server, Install-WindowsFeature UpdateServices-UI.";

    /// <summary>The API status for this computer.</summary>
    public static WsusApiStatus Probe() => Probe(Candidates());

    /// <summary>The status for the first candidate path that exists.</summary>
    public static WsusApiStatus Probe(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            string version;
            try { version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { version = ""; }
            return new WsusApiStatus(true, version, path, "");
        }
        return new WsusApiStatus(false, "", "", NotInstalledMessage);
    }

    /// <summary>The assembly cache folders first, then %ProgramW6432%\Update Services\Api.</summary>
    public static IEnumerable<string> Candidates()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var cache in new[]
                 {
                     Path.Combine(windows, "Microsoft.NET", "assembly", "GAC_MSIL", "Microsoft.UpdateServices.Administration"),
                     Path.Combine(windows, "assembly", "GAC_MSIL", "Microsoft.UpdateServices.Administration")
                 })
        {
            string[] versions;
            try { versions = Directory.Exists(cache) ? Directory.GetDirectories(cache) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { versions = []; }
            foreach (var folder in versions.OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
                yield return Path.Combine(folder, AssemblyFileName);
        }
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
        if (string.IsNullOrWhiteSpace(programFiles)) programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Update Services", "Api", AssemblyFileName);
    }
}
