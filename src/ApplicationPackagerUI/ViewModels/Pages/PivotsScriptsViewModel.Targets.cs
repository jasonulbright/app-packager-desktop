using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class PivotsScriptsViewModel
{
    public const string ResolveFirst = "Select Resolve first. A run goes only to resolved targets.";
    public const string TargetEmpty = "Type a collection, a device, a pattern or a list of names first.";
    public const string ResolveFailed = "The targets could not be resolved. The log has the details.";
    public const string ImportFailed = "The file could not be read. Check that it is a TXT or CSV file that the account can open.";
    public const string NotResolvedText = "Not resolved. Select Resolve to read the devices of the target.";
    public const string BuiltInWarning = "This is a built-in collection. A run reaches every device of the collection, and the plan asks an extra box for it.";
    public const string EveryClientWarning = "The target holds every client of the site. A run reaches every device of the site, and the plan asks an extra box for it.";

    private int _resolveGeneration;
    private string? _resolvedKey;

    public IReadOnlyList<TargetKindChoice> TargetKindChoices { get; } = Enum.GetValues<RunTargetKind>().Select(k => new TargetKindChoice(k, PivotsLabels.TargetKind(k))).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetHint), nameof(SelectedTargetKind))]
    private RunTargetKind _targetKind = RunTargetKind.Collection;

    public TargetKindChoice SelectedTargetKind
    {
        get => TargetKindChoices.First(c => c.Kind == TargetKind);
        set
        {
            if (value is not null) TargetKind = value.Kind;
        }
    }

    [ObservableProperty] private string _targetText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResolveCommand), nameof(ImportTargetsCommand))]
    private bool _isResolving;

    /// <summary>The devices of the last Resolve; null before a Resolve and after the target text or kind changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolved), nameof(ShowBuiltInWarning), nameof(EveryDeviceWarningText))]
    private ResolvedTargets? _resolved;

    public bool IsResolved => Resolved is not null;
    public bool ShowBuiltInWarning => Resolved is { ReachesEveryDevice: true };
    public string EveryDeviceWarningText => Resolved is { IsBuiltInCollection: false, CoversEverySiteDevice: true } ? EveryClientWarning : BuiltInWarning;

    [ObservableProperty] private string _targetSummary = NotResolvedText;

    public string TargetHint => TargetKind switch
    {
        RunTargetKind.Collection => "A collection ID, such as LAB00012, or the exact collection name.",
        RunTargetKind.Device => "The exact name of one device.",
        RunTargetKind.Pattern => "A device name with * for any text, such as WKS-*. The plan lists every device that matches.",
        _ => "Device names, one per line. A name that matches no device is listed and left out."
    };

    private string TargetKey() => $"{TargetKind}|{TargetText.Trim()}|{SiteKey()}";

    private string SiteKey()
    {
        var settings = Services.LoadSettings();
        return $"{settings.SiteCode?.Trim().ToUpperInvariant()}|{settings.ProviderMachine?.Trim().ToUpperInvariant()}";
    }

    partial void OnTargetKindChanged(RunTargetKind value)
    {
        TargetsChanged();
        SaveLater();
    }

    partial void OnTargetTextChanged(string value) => TargetsChanged();

    private void TargetsChanged()
    {
        if (_applying) return;
        _resolveGeneration++;
        if (_resolvedKey is not null && _resolvedKey == TargetKey()) return;
        if (Resolved is null && TargetSummary == NotResolvedText) return;
        Resolved = null;
        _resolvedKey = null;
        TargetSummary = NotResolvedText;
        UpdateSummary();
    }

    /// <summary>The resolved targets when they still belong to the target text, the kind and the site; null otherwise.</summary>
    public ResolvedTargets? CurrentTargets() => Resolved is { } targets && _resolvedKey == TargetKey() ? targets : null;

    private void ReleaseTargets()
    {
        _resolveGeneration++;
        IsResolving = false;
        Resolved = null;
        _resolvedKey = null;
        TargetSummary = NotResolvedText;
    }

    private bool CanResolve() => !IsResolving;

    /// <summary>Reads the devices of the target with one bulk read and shows the count, the unmatched names and the built-in warning.</summary>
    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task Resolve()
    {
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }
        var text = TargetText.Trim();
        if (text.Length == 0)
        {
            StatusMessage = TargetEmpty;
            return;
        }
        var target = new RunTarget(Kind: TargetKind, Text: text);
        var key = TargetKey();
        var generation = Generation;
        var mine = ++_resolveGeneration;
        var visit = VisitToken;
        IsResolving = true;
        StatusMessage = "Reading the devices of the target.";
        try
        {
            var resolved = await Task.Run(() => Services.Service.ResolveTargetsAsync(target, visit), visit);
            if (generation != Generation || mine != _resolveGeneration) return;
            Resolved = resolved;
            _resolvedKey = key;
            TargetSummary = ResolvedText(resolved);
            StatusMessage = "";
            Services.Log.Info(LogArea, $"Resolved a {TargetKind} target: {resolved.Devices.Count} devices, {resolved.Unmatched.Count} unmatched, truncated={resolved.Truncated}, builtIn={resolved.IsBuiltInCollection}.");
        }
        catch (OperationCanceledException)
        {
            if (generation == Generation && mine == _resolveGeneration) StatusMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (generation != Generation || mine != _resolveGeneration) return;
            StatusMessage = PlainMessage(ex, ResolveFailed);
            Services.Log.Warning(LogArea, $"A target could not be resolved ({FailureName(ex)}).");
        }
        finally
        {
            IsResolving = false;
            if (generation == Generation) UpdateSummary();
        }
    }

    /// <summary>The count, the description, the online count, the unmatched names, the truncation and the built-in warning of resolved targets.</summary>
    public static string ResolvedText(ResolvedTargets targets)
    {
        var online = targets.Devices.Count(d => d.IsOnline == true);
        var lines = new List<string>
        {
            $"{PivotsLabels.Count(targets.Devices.Count, "device")}: {targets.Description}. Online: {PivotsLabels.Number(online)}."
        };
        if (targets.Truncated)
            lines.Add(targets.TotalMatched > targets.Devices.Count ? $"{PivotsScriptsText.TruncatedNote} {PivotsLabels.Count(targets.TotalMatched, "device")} match." : PivotsScriptsText.TruncatedNote);
        if (targets.NonClientCount > 0) lines.Add($"Without a ConfigMgr client: {PivotsLabels.Number(targets.NonClientCount)}. A run skips them.");
        if (targets.Unmatched.Count > 0) lines.Add($"Not found ({PivotsLabels.Number(targets.Unmatched.Count)}): {PivotsScriptsText.UnmatchedText(targets.Unmatched)}");
        if (targets.IsBuiltInCollection) lines.Add(BuiltInWarning);
        else if (targets.CoversEverySiteDevice) lines.Add(EveryClientWarning);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Reads device names from a TXT or CSV file into a List target.</summary>
    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task ImportTargets()
    {
        var path = Services.Dialogs.PickTargetFile();
        if (string.IsNullOrWhiteSpace(path)) return;
        var generation = Generation;
        IReadOnlyList<string> names;
        try { names = await Task.Run(() => PivotsTargetFile.Read(path)); }
        catch (InvalidDataException ex)
        {
            if (generation == Generation) StatusMessage = $"The file was not read: {ex.Message}";
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            if (generation == Generation) StatusMessage = ImportFailed;
            Services.Log.Warning(LogArea, $"A target file could not be read ({ex.GetType().Name}).");
            return;
        }
        if (generation != Generation) return;
        if (names.Count == 0)
        {
            StatusMessage = "The file holds no device name.";
            return;
        }
        TargetKind = RunTargetKind.List;
        TargetText = string.Join(Environment.NewLine, names);
        StatusMessage = $"Read {PivotsLabels.Count(names.Count, "name")} from {Path.GetFileName(path)}. Select Resolve.";
        Services.Log.Info(LogArea, $"Read {names.Count} names from a target file.");
    }

    /// <summary>Puts the device names of the selected rows into a List target and opens the tab; the targets must be resolved again.</summary>
    private void SelectionTo(int tab)
    {
        var names = SelectedDeviceNames();
        if (names.Count == 0)
        {
            StatusMessage = "Select one or more rows in the Results or Devices tab first.";
            return;
        }
        TargetKind = RunTargetKind.List;
        TargetText = string.Join(Environment.NewLine, names);
        SelectedTabIndex = tab;
        StatusMessage = $"{PivotsLabels.Count(names.Count, "device")} set as the targets. Select Resolve.";
    }

    [RelayCommand]
    private void SelectionToPivot() => SelectionTo(PivotTab);

    [RelayCommand]
    private void SelectionToScript() => SelectionTo(ScriptTab);
}
