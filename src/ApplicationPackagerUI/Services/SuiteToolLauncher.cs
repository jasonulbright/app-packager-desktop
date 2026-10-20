using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ApplicationPackagerEngine.Services.Logging;
using Microsoft.Win32;

namespace ApplicationPackagerUI.Services;

/// <summary>A tool of the AppPackager Suite: its sidebar id and title, its install folder and entry script.</summary>
public sealed record SuiteTool(string Id, string Title, string Folder, string EntryScript, string Symbol);

/// <summary>The suite tools that have a sidebar entry, in the default sidebar order.</summary>
public static class SuiteTools
{
    /// <summary>Started from the Deployment Helper page for ring deployments; the sidebar entry opens the page.</summary>
    public static SuiteTool DeploymentHelper { get; } =
        new("deployment-helper", "Deployment Helper", "deployment-helper", "start-deploymenthelper.ps1", "Rocket24");

    public static IReadOnlyList<SuiteTool> All { get; } =
    [
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

    /// <summary>The exit code when the process ends within the limit; null while it still runs.</summary>
    Task<int?> WaitForExitAsync(TimeSpan limit);
}

/// <summary>The window calls behind <see cref="ToolWindowActivation"/>.</summary>
public interface IToolWindows
{
    /// <summary>The id of the process that created the window; 0 when the window is gone.</summary>
    int OwnerProcessId(IntPtr window);

    bool IsMinimized(IntPtr window);

    void Restore(IntPtr window);

    bool Activate(IntPtr window);
}

public static class ToolWindowActivation
{
    /// <summary>
    /// Activates the window only when the process that owns it is the started process.
    /// Windows does not reuse a process id while a handle to the process is open, so the
    /// owner check counts only while <paramref name="hasExited"/>, read through that handle,
    /// is false; the check runs after the owner lookup so an exit in between is seen.
    /// </summary>
    public static bool BringToFront(int processId, IntPtr window, Func<bool> hasExited, IToolWindows windows)
    {
        if (window == IntPtr.Zero) return false;
        if (windows.OwnerProcessId(window) != processId) return false;
        if (hasExited()) return false;
        if (windows.IsMinimized(window)) windows.Restore(window);
        return windows.Activate(window);
    }
}

/// <summary>How long a launch watches a new process and retries a window that is not open yet.</summary>
public sealed class SuiteToolLaunchTiming
{
    public TimeSpan StartWatch { get; init; } = TimeSpan.FromSeconds(2);
    public int FrontAttempts { get; init; } = 3;
    public TimeSpan FrontRetryInterval { get; init; } = TimeSpan.FromSeconds(0.75);
    public Func<TimeSpan, Task> Delay { get; init; } = Task.Delay;
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
    StillStarting,
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
    private const string LogArea = "Suite tools";

    private readonly Dictionary<string, IToolProcess> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private readonly SuiteToolLaunchTiming _timing;
    private readonly IAppLog _log;

    public SuiteToolLauncher(ISuiteFileSystem files, ISuiteEnvironment environment, IToolProcessStarter starter, IAppLog log, SuiteToolLaunchTiming? timing = null)
    {
        _log = log;
        _files = files;
        _environment = environment;
        _starter = starter;
        _timing = timing ?? new SuiteToolLaunchTiming();
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

    /// <summary>
    /// Starts the tool with the desktop's site values. A site code the operator never saved
    /// is the default, not a site, so the tool gets the one the suite launcher gave the
    /// desktop, or none.
    /// </summary>
    public Task<SuiteToolLaunchResult> LaunchAsync(SuiteTool tool, Models.AppSettings settings) => LaunchAsync(tool, () => settings);

    /// <summary>
    /// As <see cref="LaunchAsync(SuiteTool, Models.AppSettings)"/>, with the settings read on a
    /// worker thread together with the path lookup.
    /// </summary>
    public Task<SuiteToolLaunchResult> LaunchAsync(SuiteTool tool, Func<Models.AppSettings> loadSettings) =>
        LaunchCoreAsync(tool, () =>
        {
            var settings = loadSettings();
            var siteCode = settings.SiteCodeSaved == true
                ? settings.SiteCode
                : Environment.GetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable);
            return (siteCode, settings.ProviderMachine);
        });

    /// <summary>
    /// Starts the tool, or brings its running window to the front. A new process is watched
    /// for <see cref="SuiteToolLaunchTiming.StartWatch"/>: a non-zero exit in that time is a
    /// failure. A running tool with no window yet is retried before it counts as still starting.
    /// </summary>
    public Task<SuiteToolLaunchResult> LaunchAsync(SuiteTool tool, string? siteCode, string? provider) =>
        LaunchCoreAsync(tool, () => (siteCode, provider));

    /// <summary>
    /// The path lookup probes files that can sit on an unreachable share, so it runs on a
    /// worker thread. The pending marker is set before the first await and cleared only after
    /// the started process is recorded, so a second launch in between starts nothing.
    /// </summary>
    private async Task<SuiteToolLaunchResult> LaunchCoreAsync(SuiteTool tool, Func<(string? SiteCode, string? Provider)> siteValues)
    {
        IToolProcess? running;
        lock (_gate)
        {
            running = Running(tool);
            if (running is null && !_pending.Add(tool.Id)) return StillStarting(tool);
        }
        if (running is not null) return await BringToFrontAsync(tool, running).ConfigureAwait(true);

        (IToolProcess? Process, SuiteToolLaunchResult? Failure) start;
        try
        {
            start = await Task.Run(() => Start(tool, siteValues)).ConfigureAwait(true);
        }
        finally
        {
            lock (_gate) _pending.Remove(tool.Id);
        }
        if (start.Process is not { } started) return start.Failure!;

        var exitCode = await started.WaitForExitAsync(_timing.StartWatch).ConfigureAwait(true);
        if (exitCode is null) return new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Started);
        lock (_gate)
        {
            if (_running.TryGetValue(tool.Id, out var tracked) && ReferenceEquals(tracked, started)) _running.Remove(tool.Id);
        }
        (started as IDisposable)?.Dispose();
        return exitCode == 0
            ? new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Started)
            : new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Failed, $"{tool.Title} stopped right after it started (exit code {exitCode}).");
    }

    private (IToolProcess? Process, SuiteToolLaunchResult? Failure) Start(SuiteTool tool, Func<(string? SiteCode, string? Provider)> siteValues)
    {
        var (siteCode, provider) = siteValues();
        var resolution = Resolve(tool);
        if (resolution.Location is not { } location)
            return (null, new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Failed, resolution.Message));

        var powerShell = _environment.WindowsPowerShellPath;
        if (!_files.FileExists(powerShell))
            return (null, new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Failed, $"{tool.Title} needs Windows PowerShell 5.1, which was not found at {powerShell}."));

        try
        {
            var started = _starter.Start(BuildStartInfo(location, siteCode, provider));
            lock (_gate) _running[tool.Id] = started;
            return (started, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _log.Error(LogArea, $"{tool.Title} could not be started: {SecretMask.Apply(ex.ToString())}");
            return (null, new SuiteToolLaunchResult(SuiteToolLaunchOutcome.Failed, $"{tool.Title} could not be started. The log has the details."));
        }
    }

    private static SuiteToolLaunchResult StillStarting(SuiteTool tool) =>
        new(SuiteToolLaunchOutcome.StillStarting, $"{tool.Title} is still starting. Its window is not open yet.");

    private async Task<SuiteToolLaunchResult> BringToFrontAsync(SuiteTool tool, IToolProcess process)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (process.BringToFront()) return new SuiteToolLaunchResult(SuiteToolLaunchOutcome.BroughtToFront);
            if (attempt >= _timing.FrontAttempts) break;
            await _timing.Delay(_timing.FrontRetryInterval).ConfigureAwait(true);
        }
        return StillStarting(tool);
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
        public int Id { get; } = process.Id;

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
                return ToolWindowActivation.BringToFront(Id, process.MainWindowHandle, () => HasExited, Win32ToolWindows.Instance);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }

        public async Task<int?> WaitForExitAsync(TimeSpan limit)
        {
            using var watch = new CancellationTokenSource(limit);
            try
            {
                await process.WaitForExitAsync(watch.Token).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return null;
            }
        }

        public void Dispose() => process.Dispose();
    }
}

public sealed class Win32ToolWindows : IToolWindows
{
    private const int RestoreCommand = 9;

    public static Win32ToolWindows Instance { get; } = new();

    public int OwnerProcessId(IntPtr window) =>
        GetWindowThreadProcessId(window, out var processId) == 0 ? 0 : unchecked((int)processId);

    public bool IsMinimized(IntPtr window) => IsIconic(window);

    public void Restore(IntPtr window) => ShowWindow(window, RestoreCommand);

    public bool Activate(IntPtr window) => SetForegroundWindow(window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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
