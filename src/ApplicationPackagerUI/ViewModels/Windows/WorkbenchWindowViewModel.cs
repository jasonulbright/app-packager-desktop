using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// The Application Workbench window. Every editor property writes straight into a
/// <see cref="WorkbenchEditorSession"/>, which owns the inherited/custom state, dirty
/// tracking, drafts and the Save rules; this class only mirrors that state for binding.
/// </summary>
public partial class WorkbenchWindowViewModel : ObservableObject
{
    public const int ReviewSectionIndex = 6;

    private const string UnsavedText = "Unsaved changes. Save writes the profile; nothing is deployed.";
    private const string CleanText = "No unsaved changes.";
    private const string NoSealedBuilds = "No sealed builds";
    private const string SigningLine = "policy from preferences, applied at build time";

    private readonly SettingsService _settingsService;
    private readonly WorkbenchApplicationCatalog _catalog;
    private readonly IWorkbenchBuildLauncher _launcher;
    private readonly ConditionTemplateService _templateService = new();
    private ConditionTemplateDocument _templates = ConditionTemplateService.Defaults();
    private WorkbenchStore? _store;
    private WorkbenchEditorSession? _session;
    private AppSettings _settings = new();
    private bool _loading;

    public WorkbenchWindowViewModel(
        SettingsService settingsService, WorkbenchApplicationCatalog catalog, IWorkbenchBuildLauncher launcher)
    {
        _settingsService = settingsService;
        _catalog = catalog;
        _launcher = launcher;
    }

    /// <summary>Set by the window before <see cref="InitializeAsync"/>.</summary>
    public IWorkbenchPrompts? Prompts { get; set; }

    public event EventHandler? CloseRequested;

    public WorkbenchEditorSession? Session => _session;
    public bool IsDirty => _session?.IsDirty == true;

    // ── Static option lists ──────────────────────────────────────

    public IReadOnlyList<string> Sections { get; } =
        ["Application", "Install & uninstall", "Detection", "Requirements & variants", "Timing & execution", "Source files", "Review & build"];
    public IReadOnlyList<string> TitleModeOptions => WorkbenchFieldMapping.TitleModeOptions;
    public IReadOnlyList<string> ScriptTargetOptions { get; } = ["Install", "Uninstall"];
    public IReadOnlyList<string> InstallModeOptions => WorkbenchFieldMapping.ScriptModeOptions;
    public IReadOnlyList<string> RebootPolicyOptions => WorkbenchFieldMapping.RebootOptions;
    public IReadOnlyList<string> DetectionModeOptions { get; } = ["Inherit", "Custom"];
    public IReadOnlyList<string> DetectionTypeOptions { get; } = ["Registry", "File", "PowerShell", "Compound"];
    public IReadOnlyList<string> HiveOptions { get; } = ["HKLM", "HKCU", "HKCR"];
    public IReadOnlyList<string> ViewOptions { get; } = ["64-bit", "32-bit"];
    public IReadOnlyList<string> OperatorOptions { get; } = ["Exists", "Equals", "NotEquals", "GreaterEqual", "Greater", "LessEqual", "Less", "Contains"];
    public IReadOnlyList<string> VersionBindingOptions { get; } = ["Follows staged version", "Pinned"];
    public IReadOnlyList<string> FilePropertyOptions { get; } = ["Exists", "Version", "Size", "DateModified"];
    public IReadOnlyList<string> LogicOptions { get; } = ["And", "Or", "Two groups"];
    public IReadOnlyList<string> ExecContextOptions { get; } = ["System", "User"];
    public IReadOnlyList<string> ExecLogonOptions { get; } = ["Whether or not a user is logged on", "Only when a user is logged on", "Only when no user is logged on"];
    public IReadOnlyList<string> ExecInteractionOptions { get; } = ["Hidden", "Normal", "Minimized", "Maximized"];
    public IReadOnlyList<string> ExecScriptHostOptions { get; } = ["x64", "x86"];

    // ── Header ───────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<WorkbenchApplicationItem> _applications = [];
    [ObservableProperty] private WorkbenchApplicationItem? _selectedApplication;
    [ObservableProperty] private ObservableCollection<WorkbenchProfileChoice> _profiles = [];
    [ObservableProperty] private WorkbenchProfileChoice? _selectedProfile;
    [ObservableProperty] private string _sourceTypeText = "";
    [ObservableProperty] private ObservableCollection<string> _builds = ["No sealed builds"];
    [ObservableProperty] private string _selectedBuild = "No sealed builds";
    [ObservableProperty] private int _selectedSectionIndex;

    // ── Application ──────────────────────────────────────────────

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _displayNameSource = "";
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private string _publisherSource = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _descriptionSource = "";
    [ObservableProperty] private string _titleMode = "Packager default";
    [ObservableProperty] private string _titleModeSource = "";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _identityText = "";
    [ObservableProperty] private string _provenanceText = "";
    [ObservableProperty] private string _iconPreviewPath = "";
    [ObservableProperty] private string _iconStateText = "";

    // ── Install & uninstall ─────────────────────────────────────

    [ObservableProperty] private string _scriptTarget = "Install";
    [ObservableProperty] private string _installModeDisplay = "Generated";
    [ObservableProperty] private string _installModeSource = "";
    [ObservableProperty] private string _commandOverride = "";
    [ObservableProperty] private string _commandSource = "";
    [ObservableProperty] private string _inheritedCommand = "";
    [ObservableProperty] private string _returnCodes = "";
    [ObservableProperty] private string _rebootPolicy = "Inherit";
    [ObservableProperty] private string _workingDirectory = "";
    [ObservableProperty] private string _scriptCaption = "Install script";
    [ObservableProperty] private string _scriptText = "";
    [ObservableProperty] private string _scriptGutter = "1";
    [ObservableProperty] private bool _isScriptReadOnly = true;
    [ObservableProperty] private bool _hooksEnabled;
    [ObservableProperty] private string _hookBefore = "";
    [ObservableProperty] private string _hookAfter = "";
    [ObservableProperty] private bool _afterRunsOnFailure;
    [ObservableProperty] private string _scriptNote = "";

    // ── Detection ───────────────────────────────────────────────

    [ObservableProperty] private string _detectionMode = "Inherit";
    [ObservableProperty] private string _detectionType = "Registry";
    [ObservableProperty] private string _detectionSource = "";
    [ObservableProperty] private string _detectionInheritedText = "";
    [ObservableProperty] private bool _isDetectionCustom;
    [ObservableProperty] private bool _isDetectionScript;
    [ObservableProperty] private string _detHive = "HKLM";
    [ObservableProperty] private string _detView = "64-bit";
    [ObservableProperty] private string _detKey = "";
    [ObservableProperty] private string _detValueName = "";
    [ObservableProperty] private string _detOperator = "Exists";
    [ObservableProperty] private string _detExpected = "";
    [ObservableProperty] private string _detVersionBinding = "Follows staged version";
    [ObservableProperty] private string _detFilePath = "";
    [ObservableProperty] private string _detFileName = "";
    [ObservableProperty] private string _detFileProperty = "Exists";
    [ObservableProperty] private string _detLogic = "And";
    [ObservableProperty] private string _detectScript = "";
    [ObservableProperty] private string _detectGutter = "1";
    [ObservableProperty] private bool _intuneScriptConversion;
    [ObservableProperty] private DetectionClauseRow? _selectedClause;
    public ObservableCollection<DetectionClauseRow> Clauses { get; } = [];

    // ── Requirements & variants ─────────────────────────────────

    [ObservableProperty] private ObservableCollection<string> _requirementTemplates = [];
    [ObservableProperty] private string? _selectedRequirementTemplate;
    [ObservableProperty] private RequirementOperationRow? _selectedRequirement;
    public ObservableCollection<RequirementOperationRow> Requirements { get; } = [];
    [ObservableProperty] private ObservableCollection<string> _variantSplitOptions = ["None"];
    [ObservableProperty] private string _variantSplit = "None";
    [ObservableProperty] private bool _variantSplitEnabled;
    [ObservableProperty] private ObservableCollection<string> _installForOptions = ["Packager default"];
    [ObservableProperty] private string _installFor = "Packager default";
    [ObservableProperty] private bool _installForEnabled;
    [ObservableProperty] private string _variantSource = "";
    public ObservableCollection<VariantOverrideRow> Variants { get; } = [];
    [ObservableProperty] private string _siteArchGc = "";
    [ObservableProperty] private string _siteLangGc = "";
    [ObservableProperty] private string _siteVpnGc = "";
    [ObservableProperty] private string _siteVpnPatterns = "";
    [ObservableProperty] private bool _hasArchTemplate;
    [ObservableProperty] private bool _hasLangTemplate;
    [ObservableProperty] private bool _hasVpnTemplate;

    // ── Timing & execution ──────────────────────────────────────

    [ObservableProperty] private bool _estimatedUseDefault = true;
    [ObservableProperty] private string _estimatedMinutes = "";
    [ObservableProperty] private string _estimatedEffective = "";
    [ObservableProperty] private bool _maximumUseDefault = true;
    [ObservableProperty] private string _maximumMinutes = "";
    [ObservableProperty] private string _maximumEffective = "";
    [ObservableProperty] private string _execContext = "System";
    [ObservableProperty] private string _execContextSource = "";
    [ObservableProperty] private string _execLogon = "Whether or not a user is logged on";
    [ObservableProperty] private string _execInteraction = "Hidden";
    [ObservableProperty] private string _execScriptHost = "x64";

    // ── Source files ────────────────────────────────────────────

    [ObservableProperty] private SourceFileRow? _selectedSourceFile;
    public ObservableCollection<SourceFileRow> SourceFiles { get; } = [];

    // ── Review & build ──────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<WorkbenchDiffRow> _diffRows = [];
    [ObservableProperty] private ObservableCollection<WorkbenchFinding> _findings = [];
    [ObservableProperty] private string _resolvedPlan = "";
    public ObservableCollection<PriorBuildRow> PriorBuilds { get; } = [];

    // ── Footer ──────────────────────────────────────────────────

    [ObservableProperty] private string _chipContent = "Content build: Not validated";
    [ObservableProperty] private string _chipConfigMgr = "ConfigMgr: Not validated";
    [ObservableProperty] private string _chipIntune = "Intune: Not validated";
    [ObservableProperty] private string _saveStateText = CleanText;

    // ═════════════════════════════════════════════════════════════
    // Loading
    // ═════════════════════════════════════════════════════════════

    /// <summary>Lists the applications and opens the preselected one, or the first.</summary>
    public async Task InitializeAsync(string? preselectApplicationId = null)
    {
        _settings = _settingsService.Load();
        _store = WorkbenchStore.Open();
        var applications = await _catalog.LoadAsync(_store);
        if (applications.Count == 0)
        {
            if (Prompts is not null)
                await Prompts.InfoAsync("Application Workbench", "No applications were found. Load packagers or save a bring-your-own installer first.");
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        LoadSiteConditions();
        var initial = applications.FirstOrDefault(a => string.Equals(a.ApplicationId, preselectApplicationId, StringComparison.OrdinalIgnoreCase))
                      ?? applications[0];
        _loading = true;
        try
        {
            Applications = new ObservableCollection<WorkbenchApplicationItem>(applications);
            SelectedApplication = initial;
        }
        finally { _loading = false; }
        await OpenApplicationAsync(initial, null);
    }

    private async Task OpenApplicationAsync(WorkbenchApplicationItem application, string? profileId)
    {
        var inherited = WorkbenchInheritedSettings.Build(
            application.DisplayName, application.Publisher, application.Description,
            _settings.EstimatedRuntimeMins, _settings.MaximumRuntimeMins);
        _session = new WorkbenchEditorSession(_store!, application.ApplicationId, inherited);
        _session.Load(profileId);
        if (_session.PendingDraft is not null)
        {
            var recover = Prompts is not null && await Prompts.YesNoAsync("Recover Draft",
                $"Unsaved changes to \"{_session.Profile.Name}\" were found from an earlier session. Recover them?");
            if (recover) _session.RecoverDraft();
            else _session.DiscardPendingDraft();
        }

        _loading = true;
        try
        {
            SourceTypeText = application.SourceType;
            IdentityText = $"Application id {application.ApplicationId}; output folders and vendor discovery keep using the packager identity, not the displayed title.";
            ProvenanceText = string.IsNullOrWhiteSpace(application.SourcePath)
                ? "Manual updates; no vendor discovery source is configured for this application."
                : application.SourcePath;
            VariantSplitOptions = new ObservableCollection<string>(["None", .. application.SupportsVariants]);
            VariantSplitEnabled = application.SupportsVariants.Count > 0;
            InstallForOptions = new ObservableCollection<string>(WorkbenchFieldMapping.InstallForOptions(application.SupportsInstallModes));
            InstallForEnabled = application.SupportsInstallModes.Count > 0;
        }
        finally { _loading = false; }

        LoadProfileList();
        LoadBuilds();
        RefreshAll();
        SetValidatedState(false);
        SaveStateText = _session.IsDirty ? UnsavedText : CleanText;
    }

    private void LoadProfileList()
    {
        if (_session is null) return;
        _loading = true;
        try
        {
            var choices = _store!.GetProfiles(_session.ApplicationId)
                .Select(p => new WorkbenchProfileChoice(p.ProfileId, p.Name))
                .ToList();
            choices.Add(new WorkbenchProfileChoice("", "Save as...", IsSaveAs: true));
            Profiles = new ObservableCollection<WorkbenchProfileChoice>(choices);
            SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.ProfileId, _session.ProfileId, StringComparison.OrdinalIgnoreCase))
                              ?? Profiles[0];
        }
        finally { _loading = false; }
    }

    /// <summary>The sealed builds of the shown profile, newest first, for the Build picker and the Prior builds grid.</summary>
    private void LoadBuilds()
    {
        if (_session is null || _store is null) return;
        IReadOnlyList<JsonObject> records;
        try { records = BuildRecords.List(_store.DataRoot, _session.ApplicationId, _session.ProfileId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { records = []; }
        PriorBuilds.Clear();
        foreach (var record in records)
            PriorBuilds.Add(new PriorBuildRow(
                WorkbenchJson.AsText(record["BuildId"]) ?? "", WorkbenchJson.AsText(record["SoftwareVersion"]) ?? "",
                WorkbenchJson.AsText(record["ProfileRevision"]) ?? "", "Sealed " + (WorkbenchJson.AsText(record["SealedAt"]) ?? "")));
        Builds = PriorBuilds.Count == 0
            ? new ObservableCollection<string>([NoSealedBuilds])
            : new ObservableCollection<string>(PriorBuilds.Select(b => b.BuildId));
        SelectedBuild = Builds[0];
    }

    /// <summary>Re-reads every editor from the session.</summary>
    private void RefreshAll()
    {
        if (_session is null) return;
        var s = _session;
        _loading = true;
        try
        {
            DisplayName = s.Get("Application.DisplayName").Text ?? "";
            Publisher = s.Get("Application.Publisher").Text ?? "";
            Description = s.Get("Application.Description").Text ?? "";
            TitleMode = WorkbenchFieldMapping.TitleModeToDisplay(s.Get("Application.TitleMode").Text);
            ProfileName = s.Profile.Name;

            LoadScriptEditor();
            LoadDetectionEditor();

            Requirements.Clear();
            foreach (var operation in WorkbenchRequirementRow.ReadAll(s.Get("Requirements.Operations").Value))
            {
                var row = RequirementOperationRow.From(operation);
                row.Changed = WriteRequirements;
                Requirements.Add(row);
            }
            VariantSplit = WorkbenchFieldMapping.VariantSplitToDisplay(s.Get("Variants.Split").Value);
            if (!VariantSplitOptions.Contains(VariantSplit)) VariantSplit = "None";
            InstallFor = WorkbenchFieldMapping.InstallModeToDisplay(s.Get("InstallMode").Text);
            if (!InstallForOptions.Contains(InstallFor)) InstallFor = "Packager default";
            Variants.Clear();
            if (s.Get("Variants.Overrides").Value is JsonObject overrides)
            {
                foreach (var (suffix, value) in overrides)
                {
                    var row = VariantOverrideRow.From(suffix,
                        WorkbenchJson.Get(value, "InstallCommand").Text ?? "", WorkbenchJson.Get(value, "Detection").Text ?? "",
                        WorkbenchJson.Get(value, "EstimatedMinutes").Text ?? "", WorkbenchJson.Get(value, "MaximumMinutes").Text ?? "");
                    row.Changed = WriteVariants;
                    Variants.Add(row);
                }
            }

            EstimatedUseDefault = !s.IsOverridden("Timing.EstimatedMinutes");
            EstimatedMinutes = EstimatedUseDefault ? s.Inherited.ForPath("Timing.EstimatedMinutes") : s.Get("Timing.EstimatedMinutes").Text ?? "";
            MaximumUseDefault = !s.IsOverridden("Timing.MaximumMinutes");
            MaximumMinutes = MaximumUseDefault ? s.Inherited.ForPath("Timing.MaximumMinutes") : s.Get("Timing.MaximumMinutes").Text ?? "";
            ExecContext = s.Get("Execution.Context").Text ?? s.Inherited.ExecutionContext;
            ExecLogon = s.Get("Execution.LogonRequirement").Text ?? s.Inherited.LogonRequirement;
            ExecInteraction = s.Get("Execution.UserInteraction").Text ?? s.Inherited.UserInteraction;
            ExecScriptHost = s.Get("Execution.ScriptHost").Text ?? s.Inherited.ScriptHost;

            SourceFiles.Clear();
            foreach (var entry in WorkbenchSourceFiles.Read(s.Get("SourceFiles").Value))
                SourceFiles.Add(NewSourceRow(entry));
        }
        finally { _loading = false; }
        RefreshIndicators();
    }

    private void LoadScriptEditor()
    {
        var s = _session!;
        var target = ScriptTarget;
        var mode = s.Get(target + ".Mode").Text;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            InstallModeDisplay = WorkbenchFieldMapping.ScriptModeToDisplay(mode);
            CommandOverride = s.Get(target + ".Command").Text ?? "";
            InheritedCommand = s.Inherited.ForPath(target + ".Command");
            ReturnCodes = s.Get(target + ".ReturnCodes").Text ?? "";
            WorkingDirectory = s.Get(target + ".WorkingDirectory").Text ?? "";
            RebootPolicy = s.Get(target + ".RebootPolicy").Text is { Length: > 0 } reboot ? reboot : "Inherit";
            ScriptText = s.Get(target + ".ScriptText").Text ?? "";
            HookBefore = s.Get(target + ".BeforeText").Text ?? "";
            HookAfter = s.Get(target + ".AfterText").Text ?? "";
            AfterRunsOnFailure = WorkbenchJson.IsTruthy(s.Get(target + ".AfterRunsOnFailure").Value);
        }
        finally { _loading = wasLoading; }
        ScriptCaption = target + " script";
        ScriptGutter = WorkbenchFieldMapping.LineNumbers(ScriptText);
        IsScriptReadOnly = mode is not ("Extend" or "Custom");
        HooksEnabled = mode == "Extend";
        ScriptNote = WorkbenchFieldMapping.ScriptNote(mode);
    }

    private void LoadDetectionEditor()
    {
        var s = _session!;
        var custom = string.Equals(s.Get("Detection.Mode").Text, "Custom", StringComparison.OrdinalIgnoreCase);
        var rule = WorkbenchDetectionRule.FromJson(s.Get("Detection.Rule").Value);
        var wasLoading = _loading;
        _loading = true;
        try
        {
            DetectionMode = custom ? "Custom" : "Inherit";
            DetectionType = rule.Type;
            DetHive = rule.Hive;
            DetView = rule.View;
            DetKey = rule.Key;
            DetValueName = rule.ValueName;
            DetOperator = rule.Operator;
            DetExpected = rule.ExpectedValue;
            DetVersionBinding = rule.VersionBinding;
            DetFilePath = rule.FilePath;
            DetFileName = rule.FileName;
            DetFileProperty = rule.FileProperty;
            DetLogic = rule.Logic;
            DetectScript = rule.ScriptText;
            IntuneScriptConversion = WorkbenchJson.IsTruthy(s.Get("Detection.IntuneScriptConversion").Value);
            Clauses.Clear();
            foreach (var clause in rule.Clauses)
            {
                var row = DetectionClauseRow.From(clause);
                row.Changed = WriteDetectionRule;
                Clauses.Add(row);
            }
        }
        finally { _loading = wasLoading; }
        IsDetectionCustom = custom;
        IsDetectionScript = DetectionType == "PowerShell";
        DetectGutter = WorkbenchFieldMapping.LineNumbers(DetectScript);
        DetectionInheritedText = "Inherited rule: " + s.Inherited.DetectionSummary
            + ". Detection establishes installed state; eligibility rules belong in Requirements & variants.";
    }

    /// <summary>Source labels, effective readouts, the icon block and the review pane.</summary>
    private void RefreshIndicators()
    {
        if (_session is null) return;
        var s = _session;
        DisplayNameSource = s.SourceText("Application.DisplayName");
        PublisherSource = s.SourceText("Application.Publisher");
        DescriptionSource = s.SourceText("Application.Description");
        TitleModeSource = s.SourceText("Application.TitleMode");
        InstallModeSource = s.SourceText(ScriptTarget + ".Mode");
        CommandSource = s.SourceText(ScriptTarget + ".Command");
        DetectionSource = s.SourceText("Detection.Mode");
        ExecContextSource = s.SourceText("Execution.Context");
        VariantSource = s.SourceText("InstallMode");
        EstimatedEffective = $"Default: {s.Inherited.EstimatedMinutes} minutes";
        MaximumEffective = $"Default: {s.Inherited.MaximumMinutes} minutes";
        RefreshIcon();
        RefreshReview();
        OnPropertyChanged(nameof(IsDirty));
    }

    private void RefreshIcon()
    {
        var s = _session!;
        var preview = "";
        if (!s.IsOverridden("Application.Icon"))
        {
            var application = SelectedApplication;
            preview = application is null ? "" : WorkbenchInheritedIcon.Resolve(
                application.IconPackDirectory, application.IconPackBaseName, application.IconSource, application.StageSearchRoot);
            IconStateText = WorkbenchInheritedIcon.InheritedStateText(preview);
        }
        else if (s.Get("Application.Icon").Value is null)
        {
            IconStateText = "Removed: this profile publishes no icon.";
        }
        else
        {
            preview = s.Get("Application.Icon.Path").Text ?? "";
            IconStateText = "Custom icon: " + preview;
        }
        IconPreviewPath = File.Exists(preview) ? preview : "";
    }

    private void RefreshReview()
    {
        if (_session is null) return;
        DiffRows = new ObservableCollection<WorkbenchDiffRow>(WorkbenchReview.Diff(_session));
        ResolvedPlan = WorkbenchReview.Plan(_session, SigningLine);
    }

    // ═════════════════════════════════════════════════════════════
    // Edits
    // ═════════════════════════════════════════════════════════════

    /// <summary>Every edit ends here: labels, review, chips and the save-state line follow the session.</summary>
    private void AfterEdit()
    {
        RefreshIndicators();
        SetValidatedState(false);
        SaveStateText = IsDirty ? UnsavedText : CleanText;
    }

    private void EditText(string path, string value)
    {
        if (_loading || _session is null) return;
        _session.SetText(path, value);
        AfterEdit();
    }

    partial void OnDisplayNameChanged(string value) => EditText("Application.DisplayName", value);
    partial void OnPublisherChanged(string value) => EditText("Application.Publisher", value);
    partial void OnDescriptionChanged(string value) => EditText("Application.Description", value);

    partial void OnTitleModeChanged(string value)
    {
        if (_loading || _session is null) return;
        var token = WorkbenchFieldMapping.DisplayToTitleMode(value);
        if (token is null) _session.ResetField("Application.TitleMode");
        else _session.SetValue("Application.TitleMode", token);
        AfterEdit();
    }

    partial void OnProfileNameChanged(string value)
    {
        if (_loading || _session is null) return;
        _session.SetProfileName(value);
        AfterEdit();
    }

    partial void OnScriptTargetChanged(string value)
    {
        if (_session is null) return;
        LoadScriptEditor();
        RefreshIndicators();
    }

    partial void OnInstallModeDisplayChanged(string value)
    {
        if (_loading || _session is null) return;
        var token = WorkbenchFieldMapping.DisplayToScriptMode(value);
        if (token is null) _session.ResetField(ScriptTarget + ".Mode");
        else _session.SetValue(ScriptTarget + ".Mode", token);
        LoadScriptEditor();
        AfterEdit();
    }

    partial void OnCommandOverrideChanged(string value) => EditText(ScriptTarget + ".Command", value);
    partial void OnReturnCodesChanged(string value) => EditText(ScriptTarget + ".ReturnCodes", value);
    partial void OnWorkingDirectoryChanged(string value) => EditText(ScriptTarget + ".WorkingDirectory", value);
    partial void OnHookBeforeChanged(string value) => EditText(ScriptTarget + ".BeforeText", value);
    partial void OnHookAfterChanged(string value) => EditText(ScriptTarget + ".AfterText", value);

    partial void OnScriptTextChanged(string value)
    {
        ScriptGutter = WorkbenchFieldMapping.LineNumbers(value);
        EditText(ScriptTarget + ".ScriptText", value);
    }

    partial void OnRebootPolicyChanged(string value)
    {
        if (_loading || _session is null) return;
        if (string.IsNullOrEmpty(value) || value == "Inherit") _session.ResetField(ScriptTarget + ".RebootPolicy");
        else _session.SetValue(ScriptTarget + ".RebootPolicy", value);
        AfterEdit();
    }

    partial void OnAfterRunsOnFailureChanged(bool value)
    {
        if (_loading || _session is null) return;
        if (value) _session.SetValue(ScriptTarget + ".AfterRunsOnFailure", true);
        else _session.ResetField(ScriptTarget + ".AfterRunsOnFailure");
        AfterEdit();
    }

    partial void OnDetectionModeChanged(string value)
    {
        if (_loading || _session is null) return;
        if (value == "Custom")
        {
            _session.SetValue("Detection.Mode", "Custom");
            WriteDetectionRule();
        }
        else
        {
            _session.ResetSection(WorkbenchSection.Detection);
        }
        LoadDetectionEditor();
        AfterEdit();
    }

    partial void OnDetectionTypeChanged(string value)
    {
        IsDetectionScript = value == "PowerShell";
        WriteDetectionRule();
    }

    partial void OnDetHiveChanged(string value) => WriteDetectionRule();
    partial void OnDetViewChanged(string value) => WriteDetectionRule();
    partial void OnDetKeyChanged(string value) => WriteDetectionRule();
    partial void OnDetValueNameChanged(string value) => WriteDetectionRule();
    partial void OnDetOperatorChanged(string value) => WriteDetectionRule();
    partial void OnDetExpectedChanged(string value) => WriteDetectionRule();
    partial void OnDetVersionBindingChanged(string value) => WriteDetectionRule();
    partial void OnDetFilePathChanged(string value) => WriteDetectionRule();
    partial void OnDetFileNameChanged(string value) => WriteDetectionRule();
    partial void OnDetFilePropertyChanged(string value) => WriteDetectionRule();
    partial void OnDetLogicChanged(string value) => WriteDetectionRule();

    partial void OnDetectScriptChanged(string value)
    {
        DetectGutter = WorkbenchFieldMapping.LineNumbers(value);
        WriteDetectionRule();
    }

    partial void OnIntuneScriptConversionChanged(bool value)
    {
        if (_loading || _session is null || !IsDetectionCustom) return;
        _session.SetValue("Detection.IntuneScriptConversion", value);
        AfterEdit();
    }

    /// <summary>A custom rule is stored whole: every typed field and the clauses, as the editor shows them.</summary>
    private void WriteDetectionRule()
    {
        if (_loading || _session is null) return;
        if (!string.Equals(_session.Get("Detection.Mode").Text, "Custom", StringComparison.OrdinalIgnoreCase)) return;
        var rule = new WorkbenchDetectionRule
        {
            Type = DetectionType, Hive = DetHive, View = DetView, Key = DetKey, ValueName = DetValueName,
            Operator = DetOperator, ExpectedValue = DetExpected, VersionBinding = DetVersionBinding,
            FilePath = DetFilePath, FileName = DetFileName, FileProperty = DetFileProperty, Logic = DetLogic,
            ScriptText = DetectScript, Clauses = Clauses.Select(c => c.ToClause()).ToList()
        };
        _session.SetValue("Detection.Rule", rule.ToJson());
        _session.SetValue("Detection.IntuneScriptConversion", IntuneScriptConversion);
        AfterEdit();
    }

    private void WriteRequirements()
    {
        if (_loading || _session is null) return;
        _session.SetValue("Requirements.Operations",
            new JsonArray(Requirements.Select(r => (JsonNode?)r.ToRow().ToJson()).ToArray()));
        AfterEdit();
    }

    private void WriteVariants()
    {
        if (_loading || _session is null) return;
        var overrides = WorkbenchJson.NewObject();
        foreach (var row in Variants)
        {
            var entry = WorkbenchJson.NewObject();
            if (!string.IsNullOrWhiteSpace(row.InstallCommand)) entry["InstallCommand"] = row.InstallCommand;
            if (!string.IsNullOrWhiteSpace(row.Detection)) entry["Detection"] = row.Detection;
            if (int.TryParse(row.Estimated, NumberStyles.Integer, CultureInfo.InvariantCulture, out var estimated)) entry["EstimatedMinutes"] = estimated;
            if (int.TryParse(row.Maximum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum)) entry["MaximumMinutes"] = maximum;
            overrides[row.Suffix] = entry;
        }
        _session.SetValue("Variants.Overrides", overrides);
        AfterEdit();
    }

    partial void OnVariantSplitChanged(string value)
    {
        if (_loading || _session is null) return;
        _session.SetValue("Variants.Split", WorkbenchFieldMapping.DisplayToVariantSplit(value));
        AfterEdit();
    }

    partial void OnInstallForChanged(string value)
    {
        if (_loading || _session is null) return;
        _session.SetValue("InstallMode", WorkbenchFieldMapping.DisplayToInstallMode(value));
        AfterEdit();
    }

    partial void OnEstimatedUseDefaultChanged(bool value) =>
        UseDefaultChanged("Timing.EstimatedMinutes", value, v => EstimatedMinutes = v, () => EstimatedMinutes);

    partial void OnMaximumUseDefaultChanged(bool value) =>
        UseDefaultChanged("Timing.MaximumMinutes", value, v => MaximumMinutes = v, () => MaximumMinutes);

    partial void OnEstimatedMinutesChanged(string value) => MinutesChanged("Timing.EstimatedMinutes", EstimatedUseDefault, value);
    partial void OnMaximumMinutesChanged(string value) => MinutesChanged("Timing.MaximumMinutes", MaximumUseDefault, value);

    private void UseDefaultChanged(string path, bool useDefault, Action<string> setText, Func<string> getText)
    {
        if (_loading || _session is null) return;
        if (useDefault)
        {
            _session.ResetField(path);
            _loading = true;
            try { setText(_session.Inherited.ForPath(path)); }
            finally { _loading = false; }
        }
        else
        {
            _session.SetValue(path, MinutesNode(getText()));
        }
        AfterEdit();
    }

    private void MinutesChanged(string path, bool useDefault, string text)
    {
        if (_loading || _session is null || useDefault) return;
        _session.SetValue(path, MinutesNode(text));
        AfterEdit();
    }

    /// <summary>A whole number is stored as a number; anything else is kept as typed so validation can name it.</summary>
    private static JsonNode MinutesNode(string text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            ? JsonValue.Create(minutes)
            : JsonValue.Create(text ?? "");

    partial void OnExecContextChanged(string value) => ExecutionChanged("Execution.Context", value);
    partial void OnExecLogonChanged(string value) => ExecutionChanged("Execution.LogonRequirement", value);
    partial void OnExecInteractionChanged(string value) => ExecutionChanged("Execution.UserInteraction", value);
    partial void OnExecScriptHostChanged(string value) => ExecutionChanged("Execution.ScriptHost", value);

    /// <summary>Choosing the inherited value clears the override rather than pinning it.</summary>
    private void ExecutionChanged(string path, string value)
    {
        if (_loading || _session is null) return;
        if (string.IsNullOrEmpty(value) || value == _session.Inherited.ForPath(path)) _session.ResetField(path);
        else _session.SetValue(path, value);
        AfterEdit();
    }

    partial void OnSelectedSectionIndexChanged(int value)
    {
        if (value == ReviewSectionIndex) RefreshReview();
    }

    // ── Header selection ────────────────────────────────────────

    partial void OnSelectedApplicationChanged(WorkbenchApplicationItem? oldValue, WorkbenchApplicationItem? newValue)
    {
        if (_loading || newValue is null || _session is null) return;
        if (string.Equals(newValue.ApplicationId, _session.ApplicationId, StringComparison.OrdinalIgnoreCase)) return;
        _ = SwitchApplicationAsync(oldValue, newValue);
    }

    private async Task SwitchApplicationAsync(WorkbenchApplicationItem? previous, WorkbenchApplicationItem next)
    {
        if (!await ConfirmDiscardAsync("This application has unsaved changes. Save them before switching?"))
        {
            _loading = true;
            try { SelectedApplication = previous; }
            finally { _loading = false; }
            return;
        }
        await OpenApplicationAsync(next, null);
    }

    partial void OnSelectedProfileChanged(WorkbenchProfileChoice? value)
    {
        if (_loading || value is null || _session is null) return;
        _ = SwitchProfileAsync(value);
    }

    private async Task SwitchProfileAsync(WorkbenchProfileChoice choice)
    {
        RestoreProfileSelection();
        if (choice.IsSaveAs)
        {
            await SaveAsAsync();
            return;
        }
        if (string.Equals(choice.ProfileId, _session!.ProfileId, StringComparison.OrdinalIgnoreCase)) return;
        if (!await ConfirmDiscardAsync("This profile has unsaved changes. Save them before switching?")) return;
        await OpenApplicationAsync(SelectedApplication!, choice.ProfileId);
    }

    private void RestoreProfileSelection()
    {
        _loading = true;
        try
        {
            SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.ProfileId, _session?.ProfileId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _loading = false; }
    }

    // ═════════════════════════════════════════════════════════════
    // Commands
    // ═════════════════════════════════════════════════════════════

    [RelayCommand]
    private void ResetField(string? path)
    {
        if (_session is null || string.IsNullOrEmpty(path)) return;
        _session.ResetField(path);
        RefreshAll();
        AfterEdit();
    }

    [RelayCommand]
    private void ResetCommandOverride() => ResetField(ScriptTarget + ".Command");

    [RelayCommand]
    private void ResetInstallSection()
    {
        _session?.ResetSection(WorkbenchSection.Install, ScriptTarget);
        RefreshAll();
        AfterEdit();
    }

    [RelayCommand]
    private void ResetDetectionSection()
    {
        _session?.ResetSection(WorkbenchSection.Detection);
        RefreshAll();
        AfterEdit();
    }

    [RelayCommand]
    private void ResetTimingSection()
    {
        _session?.ResetSection(WorkbenchSection.Timing);
        RefreshAll();
        AfterEdit();
    }

    [RelayCommand]
    private async Task RenameProfileAsync()
    {
        if (_session is null || Prompts is null) return;
        var refusal = _session.RenameRefusal();
        if (refusal is not null)
        {
            await Prompts.InfoAsync("Rename Profile", refusal);
            return;
        }
        var name = await Prompts.NameAsync("New name for this profile", _session.Profile.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        ProfileName = name.Trim();
    }

    [RelayCommand]
    private void ChooseIcon()
    {
        if (_session is null || Prompts is null) return;
        var file = Prompts.PickFiles("Choose icon", "Image files|*.png;*.ico;*.jpg;*.jpeg;*.bmp|All files|*.*", multiselect: false).FirstOrDefault();
        if (file is null) return;
        _session.SetValue("Application.Icon", new JsonObject { ["Path"] = file, ["Asset"] = "" });
        AfterEdit();
    }

    [RelayCommand]
    private void ResetIcon() => ResetField("Application.Icon");

    [RelayCommand]
    private void RemoveIcon()
    {
        if (_session is null) return;
        _session.RemoveField("Application.Icon");
        AfterEdit();
    }

    [RelayCommand]
    private void AddClause()
    {
        var row = new DetectionClauseRow { Changed = WriteDetectionRule };
        Clauses.Add(row);
        WriteDetectionRule();
    }

    [RelayCommand]
    private void RemoveClause()
    {
        if (SelectedClause is null) return;
        Clauses.Remove(SelectedClause);
        WriteDetectionRule();
    }

    [RelayCommand]
    private void AddRequirement()
    {
        if (string.IsNullOrWhiteSpace(SelectedRequirementTemplate)) return;
        var row = RequirementOperationRow.From(WorkbenchRequirementRow.ForTemplate(SelectedRequirementTemplate));
        row.Changed = WriteRequirements;
        Requirements.Add(row);
        WriteRequirements();
    }

    [RelayCommand]
    private void ReplaceRequirement()
    {
        if (SelectedRequirement is not null) SelectedRequirement.Op = "Replace";
    }

    /// <summary>A Remove operation is kept: it suppresses a rule the packager would otherwise contribute.</summary>
    [RelayCommand]
    private void RemoveRequirement()
    {
        if (SelectedRequirement is not null) SelectedRequirement.Op = "Remove";
    }

    [RelayCommand]
    private async Task AddSourceFileAsync()
    {
        if (Prompts is null) return;
        foreach (var file in Prompts.PickFiles("Add source file", "All files|*.*", multiselect: true))
            await AddSourceEntriesAsync(file);
    }

    [RelayCommand]
    private async Task AddSourceFolderAsync()
    {
        var folder = Prompts?.PickFolder("Add every file under this folder");
        if (folder is not null) await AddSourceEntriesAsync(folder);
    }

    private async Task AddSourceEntriesAsync(string path)
    {
        IReadOnlyList<WorkbenchSourceFile> entries;
        try { entries = await Task.Run(() => WorkbenchSourceFiles.FromPath(path)); }
        catch (InvalidOperationException ex)
        {
            if (Prompts is not null) await Prompts.InfoAsync("Source Files", ex.Message);
            return;
        }
        foreach (var entry in entries) SourceFiles.Add(NewSourceRow(entry));
        WriteSourceFiles();
    }

    [RelayCommand]
    private void ReplaceSourceFile()
    {
        if (SelectedSourceFile is null || Prompts is null) return;
        var file = Prompts.PickFiles("Replace source file", "All files|*.*", multiselect: false).FirstOrDefault();
        if (file is null) return;
        SelectedSourceFile.Replace(WorkbenchSourceFiles.Replace(SelectedSourceFile.ToEntry(), file));
    }

    [RelayCommand]
    private void RemoveSourceFile()
    {
        if (SelectedSourceFile is null) return;
        SourceFiles.Remove(SelectedSourceFile);
        WriteSourceFiles();
    }

    private SourceFileRow NewSourceRow(WorkbenchSourceFile entry)
    {
        var row = SourceFileRow.From(entry);
        row.Changed = WriteSourceFiles;
        return row;
    }

    private void WriteSourceFiles()
    {
        if (_loading || _session is null) return;
        _session.SetValue("SourceFiles", WorkbenchSourceFiles.ToJson(SourceFiles.Select(r => r.ToEntry())));
        AfterEdit();
    }

    [RelayCommand]
    private async Task UseDefaultsAsync()
    {
        if (_session is null || Prompts is null) return;
        if (!await Prompts.YesNoAsync("Use Defaults",
                "Clear every override in this profile and inherit the packager defaults? Saved builds are not touched.")) return;
        _session.UseDefaults();
        RefreshAll();
        AfterEdit();
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(null);

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (_session is null || Prompts is null) return;
        var name = await Prompts.NameAsync("Name for the new profile", _session.Profile.Name + " copy");
        if (!string.IsNullOrWhiteSpace(name)) await SaveCoreAsync(name.Trim());
    }

    private async Task<bool> SaveCoreAsync(string? saveAsName)
    {
        if (_session is null || Prompts is null) return false;
        string? askedName = null;
        if (saveAsName is null && _session.IsDefaultProfile)
        {
            askedName = await Prompts.NameAsync("Name for this profile", WorkbenchEditorSession.DefaultSaveName);
            if (string.IsNullOrWhiteSpace(askedName)) return false;
        }
        var result = _session.Save(saveAsName, (_, _) => askedName);
        switch (result.Status)
        {
            case WorkbenchSaveStatus.Failed:
                await Prompts.InfoAsync("Save Failed", result.Message);
                return false;
            case WorkbenchSaveStatus.Cancelled:
                return false;
        }
        LoadProfileList();
        LoadBuilds();
        RefreshAll();
        SetValidatedState(false);
        SaveStateText = result.Message;
        return true;
    }

    [RelayCommand]
    private void Validate()
    {
        RunValidation();
        SelectedSectionIndex = ReviewSectionIndex;
        SaveStateText = "Validation inspected the profile without running anything. Results are per target.";
    }

    private IReadOnlyList<WorkbenchFinding> RunValidation()
    {
        if (_session is null) return [];
        var findings = WorkbenchFindings.Local(_session.Profile, _session.Inherited).ToList();
        findings.Add(new WorkbenchFinding(WorkbenchFinding.Info, WorkbenchFindings.AdapterNotLoadedCode,
            "The Intune compatibility check is not loaded; only local validation ran."));
        Findings = new ObservableCollection<WorkbenchFinding>(findings);
        SetValidatedState(true, findings);
        RefreshReview();
        return findings;
    }

    private void SetValidatedState(bool validated, IEnumerable<WorkbenchFinding>? findings = null)
    {
        var chips = WorkbenchFindings.Chips(findings ?? [], validated);
        ChipContent = "Content build: " + chips.Content;
        ChipConfigMgr = "ConfigMgr: " + chips.ConfigMgr;
        ChipIntune = "Intune: " + chips.Intune;
        if (!validated) Findings = [];
    }

    [RelayCommand]
    private Task StageAsync() => RunAsync(WorkbenchBuildOperation.Stage, null);

    [RelayCommand]
    private Task PublishConfigMgrAsync() => RunAsync(WorkbenchBuildOperation.Package, RunTargets.Mecm);

    [RelayCommand]
    private Task PublishIntuneAsync() => RunAsync(WorkbenchBuildOperation.Package, RunTargets.IntuneOnly);

    /// <summary>
    /// A run uses the saved profile: unsaved edits are saved or discarded first, blocking
    /// findings for the target stop it, and the window closes once the run snapshot exists.
    /// </summary>
    private async Task RunAsync(WorkbenchBuildOperation operation, string? publishTarget)
    {
        if (_session is null || Prompts is null || SelectedApplication is null) return;
        var title = operation == WorkbenchBuildOperation.Stage ? "Stage" : "Publish";
        if (_session.IsDirty && !await ConfirmDiscardAsync("Save the profile before this run? A run uses the saved profile revision.")) return;

        var target = WorkbenchBuildLauncher.TargetFor(operation, publishTarget, _settingsService.Load().Intune.DeploymentTarget);
        var blocking = WorkbenchFindings.BlockingFor(RunValidation(), target);
        if (blocking.Count > 0)
        {
            await Prompts.InfoAsync("Blocked", "This profile has blocking findings: " + blocking[0].Message);
            return;
        }
        if (!SelectedApplication.Runnable || string.IsNullOrWhiteSpace(SelectedApplication.SourcePath))
        {
            await Prompts.InfoAsync(title, "This application has no packager script. Bring-your-own applications run through the installer intake flow.");
            return;
        }

        var activeProfile = _store!.GetApplicationDefinition(_session.ApplicationId).ActiveProfileId;
        var refusal = _launcher.Launch(
            new WorkbenchBuildRequest(_session.ApplicationId, activeProfile, operation, target,
                SelectedBuild == NoSealedBuilds ? null : SelectedBuild),
            () => CloseRequested?.Invoke(this, EventArgs.Empty));
        if (refusal is not null) await Prompts.InfoAsync(refusal.Title, refusal.Message);
    }

    /// <summary>True when the caller may leave the current profile: nothing unsaved, or Save or Discard chosen.</summary>
    public async Task<bool> ConfirmDiscardAsync(string message)
    {
        if (_session is null || !_session.IsDirty || Prompts is null) return true;
        switch (await Prompts.UnsavedAsync(message))
        {
            case WorkbenchUnsavedAnswer.Save:
                return await SaveCoreAsync(null);
            case WorkbenchUnsavedAnswer.Discard:
                _session.DiscardChanges();
                RefreshAll();
                AfterEdit();
                return true;
            default:
                return false;
        }
    }

    // ── Site condition names ────────────────────────────────────

    private void LoadSiteConditions()
    {
        _templates = _templateService.Load();
        var arch = FindTemplate("cpu-arch");
        var language = FindTemplate("os-language");
        var vpn = FindTemplate("vpn-connected");
        HasArchTemplate = arch is not null;
        HasLangTemplate = language is not null;
        HasVpnTemplate = vpn is not null;
        SiteArchGc = arch?.GlobalConditionName ?? "";
        SiteLangGc = language?.GlobalConditionName ?? "";
        SiteVpnGc = vpn?.GlobalConditionName ?? "";
        SiteVpnPatterns = string.Join(", ", vpn?.AdapterPatterns ?? []);
        RequirementTemplates = new ObservableCollection<string>(_templates.Conditions.Select(c => c.Id));
        SelectedRequirementTemplate = RequirementTemplates.FirstOrDefault();
    }

    private ConditionTemplate? FindTemplate(string id) =>
        _templates.Conditions.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Shared by every application and profile, so the names persist when a field loses
    /// focus rather than with Save. The document is written only when a value changed,
    /// leaving the built-in defaults unmaterialized otherwise.
    /// </summary>
    public void SaveSiteConditions()
    {
        var dirty = false;
        void Name(string id, string value)
        {
            var template = FindTemplate(id);
            if (template is null || string.IsNullOrWhiteSpace(value) || template.GlobalConditionName == value.Trim()) return;
            template.GlobalConditionName = value.Trim();
            dirty = true;
        }
        Name("cpu-arch", SiteArchGc);
        Name("os-language", SiteLangGc);
        Name("vpn-connected", SiteVpnGc);
        var vpn = FindTemplate("vpn-connected");
        if (vpn is not null)
        {
            var patterns = SiteVpnPatterns.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (patterns.Length > 0 && !patterns.SequenceEqual(vpn.AdapterPatterns ?? []))
            {
                vpn.AdapterPatterns = patterns;
                dirty = true;
            }
        }
        if (dirty) _templateService.Save(_templates);
        LoadSiteConditions();
    }
}
