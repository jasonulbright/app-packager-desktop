using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ApplicationPackagerUI.Services;

/// <summary>A tool of the AppPackager Suite: its sidebar id and title, its install folder and entry script.</summary>
public sealed record SuiteTool(string Id, string Title, string Folder, string EntryScript, string Symbol);

/// <summary>The suite tools that have a sidebar entry, in the default sidebar order.</summary>
public static class SuiteTools
{
    public static IReadOnlyList<SuiteTool> All { get; } =
    [
        new("detection-tester", "Detection Tester", "detection-tester", "start-detectiontester.ps1", "Beaker24"),
        new("maintenance-windows", "Maintenance Windows", "maintenance-window-manager", "start-maintenancewindowmgr.ps1", "CalendarClock24"),
        new("collection-and-compliance", "Collection and Compliance", "collection-and-compliance-manager", "start-ccm.ps1", "ShieldCheckmark24"),
        new("installer-analysis", "Installer Analysis", "installer-analysis", "start-installeranalysis.ps1", "BoxSearch24"),
        new("deployment-helper", "Deployment Helper", "deployment-helper", "start-deploymenthelper.ps1", "Rocket24"),
        new("dp-content-manager", "DP Content Manager", "dp-content-manager", "start-dpcontentmgr.ps1", "Server24"),
        new("pivots-and-scripts", "Pivots and Scripts", "pivots-and-scripts", "start-pivotsandscripts.ps1", "Code24"),
        new("site-hygiene", "Site Hygiene", "site-hygiene", "start-sitehygiene.ps1", "Broom24")
    ];
}

/// <summary>Read-only file access for the suite lookup.</summary>
public interface ISuiteFileSystem
{
    bool FileExists(string path);

    /// <summary>The file's text, or null when it cannot be read.</summary>
    string? ReadText(string path);
}

public interface ISuiteEnvironment
{
    string LocalApplicationData { get; }

    /// <summary>The 64-bit Windows PowerShell 5.1 executable on a 64-bit system.</summary>
    string WindowsPowerShellPath { get; }

    /// <summary>The install folder the suite setup recorded for the current user, or null.</summary>
    string? RegisteredInstallLocation();
}

public interface IToolProcess
{
    int Id { get; }

    bool HasExited { get; }

    /// <summary>Restores and activates the tool's main window; false when it has no window yet.</summary>
    bool BringToFront();
}

public interface IToolProcessStarter
{
    /// <summary>Starts the process; throws when it cannot start.</summary>
    IToolProcess Start(ProcessStartInfo startInfo);
}

public sealed record SuiteToolLocation(string ToolFolder, string EntryScript);

public sealed record SuiteToolResolution(SuiteToolLocation? Location, string? Message);

public enum SuiteToolLaunchOutcome
{
    Started,
    BroughtToFront,
    Failed
}

public sealed record SuiteToolLaunchResult(SuiteToolLaunchOutcome Outcome, string? Message = null);

/// <summary>
/// Starts a suite tool from the AppPackager Suite install with the desktop's site code and
/// provider. The suite install is the first of the setup's registered install folder and
/// %LOCALAPPDATA%\AppPackagerSuite that holds the suite launcher. A tool is looked up under
/// the ToolsRoot of the suite settings when one is set, then under the install folder. One
/// process per tool: a tool still running is brought to the front. Nothing is written to
/// the suite folders.
/// </summary>
public sealed class SuiteToolLauncher
{
    public const string SuiteName = "AppPackager Suite";
    public const string InstallFolderName = "AppPackagerSuite";
    public const string SuiteFolder = "app-packager-suite";
    public const string SuiteLauncherScript = "start-suite.ps1";
    public const string SuiteSettingsFile = "suite.settings.json";

    private readonly ISuiteFileSystem _files;
    private readonly ISuiteEnvironment _environment;
    private readonly IToolProcessStarter _starter;
    private readonly Dictionary<string, IToolProcess> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SuiteToolLauncher(ISuiteFileSystem files, ISuiteEnvironment environment, IToolProcessStarter starter)
    {
        _files = files;
        _environment = environment;
        _starter = starter;
    }

    /// <summary>The folders searched for the suite install, in search order.</summary>
    public IReadOnlyList<string> SuiteRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (Normalize(path) is { } full && !roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }
        Add(_environment.RegisteredInstallLocation());
        Add(Path.Combine(_environment.LocalApplicationData, InstallFolderName));
        return roots;
    }

    public SuiteToolResolution Resolve(SuiteTool tool)
    {
        var roots = SuiteRoots();
        var suiteRoot = roots.FirstOrDefault(r => _files.FileExists(Path.Combine(r, SuiteFolder, SuiteLauncherScript)));
        if (suiteRoot is null)
        {
            return new SuiteToolResolution(null,
                $@"{tool.Title} needs {SuiteName}, which is not installed. Looked for {SuiteFolder}\{SuiteLauncherScript} in: {string.Join("; ", roots)}.");
        }

        var toolRoots = new List<string>();
        if (ToolsRoot(suiteRoot) is { } configured) toolRoots.Add(configured);
        if (!toolRoots.Contains(suiteRoot, StringComparer.OrdinalIgnoreCase)) toolRoots.Add(suiteRoot);

        var folders = toolRoots.Select(r => Path.Combine(r, tool.Folder)).ToList();
        foreach (var folder in folders)
        {
            var entry = Path.Combine(folder, tool.EntryScript);
            if (_files.FileExists(entry)) return new SuiteToolResolution(new SuiteToolLocation(folder, entry), null);
        }
        return new SuiteToolResolution(null,
            $"{tool.Title} is not in the {SuiteName} install. Looked for {tool.EntryScript} in: {string.Join("; ", folders)}.");
    }

    /// <summary>
    /// The start of the entry script as the suite launcher starts it. The site values travel
    /// only as environment variables; an empty value removes the variable the desktop
    /// inherited, so the tool never sees a stale site.
    /// </summary>
    public ProcessStartInfo BuildStartInfo(SuiteToolLocation location, string? siteCode, string? provider)
    {
        var info = new ProcessStartInfo
        {
            FileName = _environment.WindowsPowerShellPath,
            WorkingDirectory = location.ToolFolder,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", location.EntryScript })
            info.ArgumentList.Add(argument);
        SetOrRemove(info, SettingsService.LauncherSiteCodeVariable, siteCode);
        SetOrRemove(info, SettingsService.LauncherProviderVariable, provider);
        return info;
    }

    public SuiteToolLaunchResult Launch(SuiteTool tool, string? siteCode, string? provider)
    {
        lock (_gate)
        {
            if (Running(tool) is { } running)
            {
                running.BringToFront();
                return new SuiteToolLaunchResult(SuiteToolLaunchOutcome.BroughtToFront);
            }

            var resolution = Resolve(tool);
            if (resolution.Location is not { } location)
                return new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Failed, resolution.Message);

            var powerShell = _environment.WindowsPowerShellPath;
            if (!_files.FileExists(powerShell))
                return new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Failed, $"{tool.Title} needs Windows PowerShell 5.1, which was not found at {powerShell}.");

            try
            {
                _running[tool.Id] = _starter.Start(BuildStartInfo(location, siteCode, provider));
                return new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Started);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Failed, $"{tool.Title} could not start: {ex.Message}");
            }
        }
    }

    /// <summary>The process id of the tool started from here while it runs; null once it exits.</summary>
    public int? RunningProcessId(SuiteTool tool)
    {
        lock (_gate) return Running(tool)?.Id;
    }

    private IToolProcess? Running(SuiteTool tool)
    {
        if (!_running.TryGetValue(tool.Id, out var process)) return null;
        if (!process.HasExited) return process;
        _running.Remove(tool.Id);
        (process as IDisposable)?.Dispose();
        return null;
    }

    private string? ToolsRoot(string suiteRoot)
    {
        var settings = Path.Combine(suiteRoot, SuiteFolder, SuiteSettingsFile);
        if (!_files.FileExists(settings) || _files.ReadText(settings) is not { } text) return null;
        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('﻿'));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("ToolsRoot", out var value)
                || value.ValueKind != JsonValueKind.String) return null;
            var root = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(root) || root.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || root.Contains('|')) return null;
            // The suite launcher's shortcut starts in its own folder, so a relative
            // ToolsRoot is relative to that folder.
            return Normalize(Path.IsPathRooted(root) ? root : Path.Combine(suiteRoot, SuiteFolder, root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static void SetOrRemove(ProcessStartInfo info, string name, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) info.Environment.Remove(name);
        else info.Environment[name] = trimmed;
    }
}

public sealed class SuiteFileSystem : ISuiteFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

public sealed class SuiteEnvironment : ISuiteEnvironment
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + SuiteToolLauncher.InstallFolderName;

    public string LocalApplicationData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // A 32-bit process reading System32 is redirected to SysWOW64, whose powershell.exe
    // runs the tools 32-bit and cannot load the 64-bit ConfigMgr console module.
    public string WindowsPowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem ? "Sysnative" : "System32",
        "WindowsPowerShell", "v1.0", "powershell.exe");

    public string? RegisteredInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class ToolProcessStarter : IToolProcessStarter
{
    public IToolProcess Start(ProcessStartInfo startInfo) =>
        new ToolProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("Windows PowerShell did not start."));

    private sealed class ToolProcess(Process process) : IToolProcess, IDisposable
    {
        private const int RestoreCommand = 9;

        public int Id => process.Id;

        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return true; }
            }
        }

        public bool BringToFront()
        {
            try
            {
                process.Refresh();
                var window = process.MainWindowHandle;
                if (window == IntPtr.Zero) return false;
                if (IsIconic(window)) ShowWindow(window, RestoreCommand);
                return SetForegroundWindow(window);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }

        public void Dispose() => process.Dispose();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);
    }
}
