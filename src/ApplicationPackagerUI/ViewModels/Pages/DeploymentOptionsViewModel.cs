using ApplicationPackagerEngine.Services.Logging;
using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Options tab: the audit log path with the result of its write probe, the templates (New, Edit
/// and Delete for user templates) and the site connection the page uses.
/// </summary>
public sealed partial class DeploymentOptionsViewModel(DeploymentShared shared) : DeploymentTabViewModel(shared)
{
    public const string ReadFailedText = "The options could not be read. The log has the details.";
    public const string PathNotRooted = "Enter a full path, for example D:\\Logs\\deployment-audit.jsonl or \\\\server\\share\\deployment-audit.jsonl, or leave it empty for the default.";
    public const string ShippedReadOnly = "Shipped templates cannot be changed or deleted. Save a copy under another name.";
    public const string TemplateDeleteFailed = "The template could not be deleted. The log has the details.";

    private bool _busy;

    protected override bool NeedsSite => false;
    protected override string ReadFailed => ReadFailedText;

    /// <summary>Writes the audit log path to the page choices; set by the page.</summary>
    public Func<string, Task>? SaveAuditPath { get; set; }

    /// <summary>The saved audit log path; set by the page.</summary>
    public Func<string>? SavedAuditPath { get; set; }

    /// <summary>Raised after a template was added, changed or deleted.</summary>
    public event EventHandler? TemplatesChanged;

    public ObservableCollection<DeploymentTemplateRow> Templates { get; } = [];

    [ObservableProperty] private string _auditLogPath = "";
    [ObservableProperty] private string _auditLogInUse = "";
    [ObservableProperty] private string _probeText = "";
    [ObservableProperty] private bool _probeWritable;
    [ObservableProperty] private string _connectionText = "";
    [ObservableProperty] private string _templateMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditTemplateCommand), nameof(DeleteTemplateCommand))]
    private DeploymentTemplateRow? _selectedTemplate;

    protected override Task<Action> ReadAsync(CancellationToken ct)
    {
        var templates = Services.Templates.All();
        var message = Services.Templates.Message;
        var probe = Services.AuditLog.Probe();
        var path = Services.AuditLog.Path;
        var settings = Services.LoadSettings();
        var problem = CollectionComplianceViewModel.ConnectionProblem(settings);
        return Task.FromResult<Action>(() =>
        {
            ShowTemplates(templates, message);
            AuditLogPath = SavedAuditPath?.Invoke() ?? "";
            ShowProbe(probe, path);
            ConnectionText = problem ?? $"Site code {settings.SiteCode}, SMS Provider {(settings.ProviderMachine.Length > 0 ? settings.ProviderMachine : "(this computer)")}. Change them in Settings, ConfigMgr Site Settings.";
        });
    }

    private void ShowProbe(AuditProbe probe, string path)
    {
        AuditLogInUse = path;
        ProbeWritable = probe.Writable;
        ProbeText = probe.Writable
            ? "The audit log can be written. A deployment can be created."
            : $"The audit log cannot be written, so no deployment can be created. {probe.Message}".TrimEnd();
    }

    private void ShowTemplates(IReadOnlyList<DeploymentTemplate> templates, string? message)
    {
        var selectedId = SelectedTemplate?.Template.Id;
        Templates.Clear();
        foreach (var t in templates.OrderBy(t => t.IsSeeded ? 0 : 1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) Templates.Add(new DeploymentTemplateRow(t));
        SelectedTemplate = Templates.FirstOrDefault(t => t.Template.Id == selectedId);
        TemplateMessage = message ?? "";
    }

    protected override void ReleaseData()
    {
        Templates.Clear();
        SelectedTemplate = null;
        AuditLogPath = "";
        AuditLogInUse = "";
        ProbeText = "";
        ProbeWritable = false;
        ConnectionText = "";
        TemplateMessage = "";
    }

    /// <summary>Why the path cannot be used, or null; an empty path selects the default.</summary>
    public static string? PathProblem(string path)
    {
        var text = path.Trim();
        if (text.Length == 0) return null;
        if (text.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(text) || text.EndsWith('\\') || text.EndsWith('/')) return PathNotRooted;
        return null;
    }

    /// <summary>Saves the path and probes the log it selects.</summary>
    [RelayCommand]
    private async Task SavePath()
    {
        if (_busy) return;
        if (PathProblem(AuditLogPath) is { } problem)
        {
            ProbeText = problem;
            ProbeWritable = false;
            return;
        }
        _busy = true;
        try
        {
            AuditLogPath = AuditLogPath.Trim();
            if (SaveAuditPath is { } save) await save(AuditLogPath);
            await ProbeAsync();
        }
        finally { _busy = false; }
    }

    [RelayCommand]
    private Task CheckLog() => ProbeAsync();

    private async Task ProbeAsync()
    {
        var generation = Shared.Generation;
        var (probe, path) = await Task.Run(() => (Services.AuditLog.Probe(), Services.AuditLog.Path));
        if (generation != Shared.Generation) return;
        ShowProbe(probe, path);
        Services.Log.Info(DeploymentShared.LogArea, $"Audit log probe: writable={probe.Writable}.");
    }

    [RelayCommand]
    private void NewTemplate() =>
        Services.Dialogs.ShowTemplateEditor(new DeploymentTemplateEditorViewModel(null, Templates.Select(t => t.Template).ToList(), SaveTemplateAsync));

    private bool CanChangeTemplate() => SelectedTemplate is not null;

    [RelayCommand(CanExecute = nameof(CanChangeTemplate))]
    private void EditTemplate()
    {
        if (SelectedTemplate?.Template is not { } template) return;
        if (template.IsSeeded)
        {
            TemplateMessage = ShippedReadOnly;
            return;
        }
        Services.Dialogs.ShowTemplateEditor(new DeploymentTemplateEditorViewModel(template, Templates.Select(t => t.Template).ToList(), SaveTemplateAsync));
    }

    [RelayCommand(CanExecute = nameof(CanChangeTemplate))]
    private async Task DeleteTemplate()
    {
        if (SelectedTemplate?.Template is not { } template) return;
        if (template.IsSeeded)
        {
            TemplateMessage = ShippedReadOnly;
            return;
        }
        if (!await Services.Dialogs.ConfirmDeleteTemplateAsync(template.Name)) return;
        try { await Task.Run(() => Services.Templates.Delete(template.Id)); }
        catch (Exception ex)
        {
            TemplateMessage = ex is DeploymentServiceException d ? d.Message : TemplateDeleteFailed;
            Services.Log.Warning(DeploymentShared.LogArea, $"A template could not be deleted ({ex.GetType().Name}).");
            return;
        }
        Services.Log.Info(DeploymentShared.LogArea, "Deleted a user template.");
        await ReloadTemplatesAsync();
        TemplateMessage = $"Deleted the template {template.Name}.";
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> SaveTemplateAsync(DeploymentTemplateEditorViewModel editor, DeploymentTemplate template)
    {
        try
        {
            await Task.Run(() =>
            {
                Services.Templates.Save(template);
                if (editor.Original is { } original && !string.Equals(original.Id, template.Id, StringComparison.OrdinalIgnoreCase))
                    Services.Templates.Delete(original.Id);
            });
        }
        catch (Exception ex)
        {
            editor.ShowMessage(ex is DeploymentServiceException d ? d.Message : DeploymentFormViewModel.TemplateSaveFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"A template could not be saved ({ex.GetType().Name}).");
            return false;
        }
        Services.Log.Info(DeploymentShared.LogArea, "Saved a user template.");
        await ReloadTemplatesAsync();
        SelectedTemplate = Templates.FirstOrDefault(t => t.Template.Id == template.Id);
        TemplateMessage = $"Saved the template {template.Name}.";
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task ReloadTemplatesAsync()
    {
        var generation = Shared.Generation;
        var (templates, message) = await Task.Run(() => (Services.Templates.All(), Services.Templates.Message));
        if (generation == Shared.Generation) ShowTemplates(templates, message);
    }
}
