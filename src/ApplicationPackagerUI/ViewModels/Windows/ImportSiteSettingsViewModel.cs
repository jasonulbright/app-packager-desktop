using System.Collections.ObjectModel;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One line of the import checklist: an item, its values in the source and the target, and what the import does with it.</summary>
public partial class ImportSettingRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public ImportSettingRowViewModel(SiteImportItem item, Action changed)
    {
        Item = item;
        _changed = changed;
        _selected = item.DefaultOn;
    }

    public SiteImportItem Item { get; }
    public string Id => Item.Id;
    public string Label => Item.Label;
    public string Kind => Item.EnvironmentValue ? "Environment value" : "Site option";
    public string Note => Item.Note;

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private bool _isConflict;

    /// <summary>0 = keep the target value (the default), 1 = replace it with the source value.</summary>
    [ObservableProperty] private int _choiceIndex;

    public SiteImportChoice Choice => ChoiceIndex == 1 ? SiteImportChoice.Replace : SiteImportChoice.Keep;

    partial void OnSelectedChanged(bool value) => _changed();
    partial void OnChoiceIndexChanged(int value) => _changed();

    public string ToolTipText =>
        $"{Label}. {Note} Tick it to copy it from the source site into the active site.";
}

/// <summary>A target of a site-file import: an existing site (merge) or a new site that the import creates.</summary>
public sealed record SiteFileTarget(string FolderName, string Text, bool IsNew);

/// <summary>
/// The Import settings window (Q-112e). The user chooses a source site, ticks the site-scoped items to copy, and sees the preview
/// (what each tick does, and the conflicts with values that the active site already holds) before anything is written. The source
/// site is never changed. Secrets, the site code and the records of the source site are not offered.
/// </summary>
public partial class ImportSiteSettingsViewModel : ObservableObject
{
    public const string Title = "Import settings from another site";
    public static readonly string[] ChoiceTexts = ["Keep target value", "Replace with source value"];

    private readonly string? _dataRoot;
    private bool _loading = true;

    public ImportSiteSettingsViewModel(IReadOnlyList<SiteInfo> sources, string targetFolder, string? dataRoot = null)
    {
        _dataRoot = dataRoot;
        TargetFolder = targetFolder;
        TargetText = SiteCatalog.Get(targetFolder, dataRoot)?.DisplayName ?? targetFolder;
        Sources = new ObservableCollection<SiteInfo>(sources);
        foreach (var item in SiteSettingsImport.Items) Rows.Add(new ImportSettingRowViewModel(item, Refresh));
        NeverCopied = SiteSettingsImport.NeverCopied.Select(n => $"{n.Label}: {n.Reason}").ToList();
        _loading = false;
        if (Sources.Count > 0) SelectedSource = Sources[0];
        else Refresh();
    }

    private readonly SiteFileContent? _file;

    /// <summary>The window for a site file (Q-112h): the source is the file, the target is an existing site (merge) or a new site.</summary>
    public ImportSiteSettingsViewModel(SiteFileContent file, string? defaultTargetFolder, string? dataRoot = null)
    {
        _dataRoot = dataRoot;
        _file = file;
        SourceText = $"Site file: {file.SiteCode}{(file.Label.Length > 0 ? " - " + file.Label : "")}" +
                     (file.CreatedUtc.Length > 0 ? $" (exported {file.CreatedUtc})" : "");
        var existing = SiteCatalog.List(dataRoot);
        foreach (var site in existing)
            Targets.Add(new SiteFileTarget(site.FolderName, $"Merge into {site.DisplayName}", false));
        var fresh = SiteFile.NewFolderNameFor(file, dataRoot);
        Targets.Add(new SiteFileTarget(fresh, existing.Any(s => s.SiteCode == file.SiteCode)
            ? $"New site {fresh} (a different environment with the code {file.SiteCode})"
            : $"New site {fresh}", true));
        foreach (var item in SiteSettingsImport.Items) Rows.Add(new ImportSettingRowViewModel(item, Refresh));
        NeverCopied = SiteSettingsImport.NeverCopied.Select(n => $"{n.Label}: {n.Reason}").ToList();
        TargetFolder = "";
        TargetText = "";
        _loading = false;
        // A site that has the file's code is the natural target; with none, the new site; otherwise the active site.
        SelectedTarget = Targets.FirstOrDefault(t => t.FolderName == defaultTargetFolder && !t.IsNew && existing.Any(s => s.FolderName == t.FolderName && s.SiteCode == file.SiteCode))
                         ?? Targets.FirstOrDefault(t => t.IsNew && !existing.Any(s => s.SiteCode == file.SiteCode))
                         ?? Targets.FirstOrDefault(t => !t.IsNew && t.FolderName == defaultTargetFolder)
                         ?? Targets[0];
    }

    public bool IsFileMode => _file is not null;
    public bool IsSiteMode => _file is null;
    public string WindowTitle => IsFileMode ? "Import a site file" : Title;
    public string SourceLabel => IsFileMode ? "Copy from (site file)" : "Copy from (source site)";
    public string TargetLabel => IsFileMode ? "Copy into (target site)" : "Copy into (active site)";
    public string SourceText { get; } = "";
    public ObservableCollection<SiteFileTarget> Targets { get; } = [];
    [ObservableProperty] private SiteFileTarget? _selectedTarget;

    partial void OnSelectedTargetChanged(SiteFileTarget? value)
    {
        TargetFolder = value?.FolderName ?? "";
        TargetText = value?.Text ?? "";
        Refresh();
    }

    public string TargetFolder { get; private set; }
    public string TargetText { get; private set; }
    public ObservableCollection<SiteInfo> Sources { get; }
    public ObservableCollection<ImportSettingRowViewModel> Rows { get; } = [];
    public IReadOnlyList<string> NeverCopied { get; }
    public IReadOnlyList<string> Choices => ChoiceTexts;

    [ObservableProperty] private SiteInfo? _selectedSource;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private bool _canImport;

    /// <summary>The report of the run, after Import. Null until the user imported.</summary>
    public SiteImportReport? Result { get; private set; }

    /// <summary>The last preview.</summary>
    public SiteImportReport? Preview { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnSelectedSourceChanged(SiteInfo? value) => Refresh();

    /// <summary>Plans the import with the current source, ticks and choices. Writes nothing.</summary>
    public void Refresh()
    {
        if (_loading) return;
        if (!IsFileMode && SelectedSource is null)
        {
            Summary = "Choose the site to copy the settings from.";
            CanImport = false;
            return;
        }
        if (IsFileMode && SelectedTarget is null) return;
        var report = SiteSettingsImport.Plan(BuildRequest());
        Preview = report;
        if (report.Outcome == SiteImportReport.Refused)
        {
            ValidationMessage = report.Reason;
            Summary = "";
            CanImport = false;
            foreach (var row in Rows) { row.SourceText = ""; row.TargetText = ""; row.State = ""; row.PlanText = ""; row.IsConflict = false; }
            return;
        }
        ValidationMessage = "";
        foreach (var r in report.Rows)
        {
            var row = Rows.First(x => x.Id == r.Id);
            row.SourceText = r.SourceText;
            row.TargetText = r.TargetText;
            row.State = r.State;
            row.IsConflict = r.State == SiteImportState.Conflict;
            row.PlanText = PlanText(r);
        }
        var copy = report.Count(SiteImportOutcome.Copy);
        var replace = report.Count(SiteImportOutcome.Replace);
        var keep = report.Count(SiteImportOutcome.Keep);
        var already = report.Count(SiteImportOutcome.AlreadyDone);
        Summary = copy + replace + keep + already == 0
            ? "Nothing is ticked that the source site has set."
            : $"The import will copy {copy}, replace {replace} and keep {keep}; {already} already the same. The source site is not changed.";
        CanImport = copy + replace > 0;
    }

    private static string PlanText(SiteImportRow r) => r.Outcome switch
    {
        SiteImportOutcome.Copy => "Will copy",
        SiteImportOutcome.Replace => "Will replace",
        SiteImportOutcome.Keep => "Keeps the target value",
        SiteImportOutcome.AlreadyDone => "Already the same",
        SiteImportOutcome.NotInSource => "Not set in the source",
        SiteImportOutcome.Skipped => "Not ticked",
        _ => r.Outcome,
    };

    private SiteImportRequest BuildRequest() => _file is not null
        ? SiteFile.ImportRequest(_file, TargetFolder, Rows.Where(r => r.Selected).Select(r => r.Id).ToList(),
            Rows.ToDictionary(r => r.Id, r => r.Choice), _dataRoot)
        : new(
        SelectedSource!.FolderName, TargetFolder,
        Rows.Where(r => r.Selected).Select(r => r.Id).ToList(),
        Rows.ToDictionary(r => r.Id, r => r.Choice),
        _dataRoot);

    [RelayCommand]
    private void Import()
    {
        if (!IsFileMode && SelectedSource is null) return;
        if (IsFileMode && SelectedTarget is null) return;
        var report = SiteSettingsImport.Run(BuildRequest());
        Result = report;
        if (report.Outcome is SiteImportReport.Refused or SiteImportReport.FailedOutcome)
        {
            ValidationMessage = report.Reason;
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The message after a run: what was copied, replaced and kept, and where the log is.</summary>
    public static string ResultMessage(SiteImportReport report, string target, bool fromFile = false)
    {
        if (report.Outcome == SiteImportReport.Refused || report.Outcome == SiteImportReport.FailedOutcome) return report.Reason;
        var copied = report.Count(SiteImportOutcome.Copied);
        var replaced = report.Count(SiteImportOutcome.Replaced);
        var kept = report.Count(SiteImportOutcome.Kept);
        var text = report.Outcome == SiteImportReport.NothingToDo
            ? $"Nothing changed in site {target}. {report.Reason}"
            : fromFile
                ? $"Imported the site file of {report.SourceFolder} into site {target}: {copied} copied, {replaced} replaced, {kept} kept. The file is unchanged."
                : $"Imported from site {report.SourceFolder} into site {target}: {copied} copied, {replaced} replaced, {kept} kept. The source site is unchanged.";
        return report.Warning.Length > 0 ? text + " " + report.Warning : text;
    }
}
