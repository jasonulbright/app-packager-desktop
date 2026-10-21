using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The ring folders of the Options tab: the folder of the plan files and the folder of the run files,
/// with a check that shows whether each can be written, whether a lock file can be made and whether
/// the run folder is a share.
/// </summary>
public sealed partial class RingFolderOptionsViewModel(DeploymentShared shared) : ObservableObject
{
    public const string FolderNotRooted = "Enter a full folder path, for example D:\\Deploy\\Rings or \\\\server\\share\\Rings, or leave it empty for the default.";
    public const string FoldersNotSaved = "The folders were not saved. The settings file could not be written. The log has the details.";
    public const string ProbeFailed = "The folders could not be checked. The log has the details.";

    private bool _busy;
    private int _generation;

    public DeploymentShared Shared { get; } = shared;

    /// <summary>The saved folders; set by the page.</summary>
    public Func<(string Rings, string Runs)>? Saved { get; init; }

    /// <summary>Writes the folders to the page choices; false when nothing was written. Set by the page.</summary>
    public Func<string, string, Task<bool>>? Save { get; init; }

    [ObservableProperty] private string _ringsFolder = "";
    [ObservableProperty] private string _runsFolder = "";
    [ObservableProperty] private string _planFolderInUse = "";
    [ObservableProperty] private string _runFolderInUse = "";
    [ObservableProperty] private string _probeText = "";
    [ObservableProperty] private string _staleLockText = "";
    [ObservableProperty] private bool _probeWritable;

    /// <summary>Why the folder cannot be used, or null; an empty folder selects the default.</summary>
    public static string? FolderProblem(string folder)
    {
        var text = folder.Trim();
        if (text.Length == 0) return null;
        if (text.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(text)) return FolderNotRooted;
        return null;
    }

    /// <summary>Probes the folders off the UI thread; null when the probe failed.</summary>
    public RingFolderProbe? ProbeNow()
    {
        try { return Shared.Services.Rings.ProbeFolders(); }
        catch (Exception ex)
        {
            Shared.Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the folders could not be checked ({DeploymentShared.FailureName(ex)}).");
            return null;
        }
    }

    /// <summary>Shows the saved folders and the probe the Options tab read.</summary>
    public void Show(RingFolderProbe? probe)
    {
        var (rings, runs) = Saved?.Invoke() ?? ("", "");
        RingsFolder = rings;
        RunsFolder = runs;
        ShowProbe(probe);
    }

    public static string AgeText(TimeSpan age) =>
        age.TotalHours >= 1 && age.Minutes == 0 ? DeploymentText.Count((int)age.TotalHours, "hour")
        : age.TotalMinutes >= 1 ? DeploymentText.Count((int)Math.Round(age.TotalMinutes), "minute")
        : DeploymentText.Count((int)Math.Round(age.TotalSeconds), "second");

    private void ShowProbe(RingFolderProbe? probe)
    {
        if (probe is null)
        {
            ProbeText = ProbeFailed;
            ProbeWritable = false;
            return;
        }
        PlanFolderInUse = probe.PlanFolder;
        RunFolderInUse = probe.RunFolder;
        ProbeWritable = probe.PlanFolderWritable && probe.RunFolderWritable && probe.LockCreatable;
        var parts = new List<string>
        {
            probe.PlanFolderWritable ? "The plan folder can be written." : "The plan folder cannot be written: plans cannot be saved.",
            probe.RunFolderWritable ? "The run folder can be written." : "The run folder cannot be written: no run can start.",
            probe.LockCreatable ? "A lock file can be made in the run folder." : "No lock file can be made in the run folder: Promote, Reconcile, Close and Undo run are refused.",
            probe.IsUnc
                ? "The run folder is a share: the locks protect every session that uses this folder."
                : "The run folder is on this computer: the locks protect only the sessions that use this folder."
        };
        if (probe.Message.Length > 0) parts.Add(SecretMask.Apply(probe.Message));
        ProbeText = string.Join(" ", parts);
        StaleLockText = $"A lock file older than {AgeText(probe.StaleLockAge)} is reported as stale. It is never removed automatically: remove it by hand when the message names a session that ended.";
    }

    [RelayCommand]
    private async Task SaveFolders()
    {
        if (_busy) return;
        if ((FolderProblem(RingsFolder) ?? FolderProblem(RunsFolder)) is { } problem)
        {
            ProbeText = problem;
            ProbeWritable = false;
            return;
        }
        _busy = true;
        try
        {
            RingsFolder = RingsFolder.Trim();
            RunsFolder = RunsFolder.Trim();
            if (Save is { } save && !await save(RingsFolder, RunsFolder))
            {
                ProbeText = FoldersNotSaved;
                ProbeWritable = false;
                return;
            }
            Shared.Services.Log.Info(DeploymentShared.LogArea, "Rings: saved the ring folders.");
            await ProbeAsync();
        }
        finally { _busy = false; }
    }

    [RelayCommand]
    private Task CheckFolders() => ProbeAsync();

    private async Task ProbeAsync()
    {
        var mine = ++_generation;
        var generation = Shared.Generation;
        var probe = await Task.Run(ProbeNow);
        if (mine != _generation || generation != Shared.Generation) return;
        ShowProbe(probe);
        Shared.Services.Log.Info(DeploymentShared.LogArea, $"Rings: folder check: writable={probe?.RunFolderWritable == true} lock={probe?.LockCreatable == true}.");
    }

    public void Release()
    {
        _generation++;
        RingsFolder = "";
        RunsFolder = "";
        PlanFolderInUse = "";
        RunFolderInUse = "";
        ProbeText = "";
        StaleLockText = "";
        ProbeWritable = false;
    }
}
