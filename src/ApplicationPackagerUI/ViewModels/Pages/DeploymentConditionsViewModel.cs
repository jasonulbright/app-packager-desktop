using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class DeploymentConditionsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly PackagerDiscoveryService _discoveryService;
    private readonly ConditionTemplateService _templateService;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;

    private ConditionTemplateDocument _templates = ConditionTemplateService.Defaults();

    [ObservableProperty] private ObservableCollection<DeploymentConditionRow> _rows = [];

    // Condition template names, editable so an operator can attach to a
    // condition the site already carries under another name.
    [ObservableProperty] private string _architectureConditionName = "";
    [ObservableProperty] private string _languageConditionName = "";
    [ObservableProperty] private string _vpnConditionName = "";
    [ObservableProperty] private string _vpnAdapterPatterns = "";

    [ObservableProperty] private bool _hasArchitectureTemplate;
    [ObservableProperty] private bool _hasLanguageTemplate;
    [ObservableProperty] private bool _hasVpnTemplate;

    public string[] ArchitectureOptions => DeploymentConditionRow.ArchitectureOptions;
    public string[] NetworkOptions => DeploymentConditionRow.NetworkOptions;
    public string[] InstallModeOptions => DeploymentConditionRow.InstallModeOptions;

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand<DeploymentConditionRow> EditCommandsCommand { get; }

    public DeploymentConditionsViewModel(
        SettingsService settingsService,
        PackagerDiscoveryService discoveryService,
        ConditionTemplateService templateService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _settingsService = settingsService;
        _discoveryService = discoveryService;
        _templateService = templateService;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        EditCommandsCommand = new AsyncRelayCommand<DeploymentConditionRow>(EditCommandsAsync);

        _ = LoadCommand.ExecuteAsync(null);
    }

    private async Task LoadAsync()
    {
        _templates = _templateService.Load();
        var arch = FindTemplate("cpu-arch");
        var language = FindTemplate("os-language");
        var vpn = FindTemplate("vpn-connected");

        HasArchitectureTemplate = arch is not null;
        HasLanguageTemplate = language is not null;
        HasVpnTemplate = vpn is not null;
        ArchitectureConditionName = arch?.GlobalConditionName ?? "";
        LanguageConditionName = language?.GlobalConditionName ?? "";
        VpnConditionName = vpn?.GlobalConditionName ?? "";
        VpnAdapterPatterns = string.Join(", ", vpn?.AdapterPatterns ?? []);

        var settings = _settingsService.Load();
        var definitions = await _discoveryService.LoadDefinitionsAsync();

        var rows = new ObservableCollection<DeploymentConditionRow>();
        foreach (var def in definitions)
        {
            var supported = (def.SupportsVariants ?? [])
                .Where(DeploymentConditionValues.IsSplit)
                .ToArray();

            var row = new DeploymentConditionRow
            {
                DefinitionId = def.Id,
                Application = def.Application,
                Vendor = def.Vendor,
                VariantOptions = [DeploymentConditionValues.NoSplit, .. supported],
                InstallModeCapable = def.OffersInstallModes
            };

            if (settings.DeploymentConditions.TryGetValue(def.Id, out var entry))
            {
                row.ArchitectureDisplay = DeploymentConditionRow.ArchitectureToDisplay(entry.Architecture);
                row.NetworkDisplay = DeploymentConditionRow.NetworkToDisplay(entry.Network);
                row.LanguagesDisplay = string.Join(", ", entry.Languages ?? []);
                // A split the definition no longer declares falls back to None.
                if (supported.Contains(entry.Split))
                    row.SplitDisplay = entry.Split;
                if (row.InstallModeCapable)
                    row.InstallModeDisplay = DeploymentConditionRow.InstallModeToDisplay(entry.InstallMode);
            }

            if (settings.CommandOverrides.TryGetValue(def.Id, out var commands))
            {
                row.CommandInstall = commands.Install ?? "";
                row.CommandUninstall = commands.Uninstall ?? "";
            }

            rows.Add(row);
        }

        Rows = rows;
    }

    private ConditionTemplate? FindTemplate(string id) =>
        _templates.Conditions.FirstOrDefault(
            c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private async Task EditCommandsAsync(DeploymentConditionRow? row)
    {
        if (row is null) return;

        var panel = new CommandOverridePanel(row.Application, row.CommandInstall, row.CommandUninstall);
        var dialog = new ContentDialog
        {
            Title = $"Command overrides — {row.Application}",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await _contentDialogService.ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary)
            return;

        row.CommandInstall = panel.Install;
        row.CommandUninstall = panel.Uninstall;
    }

    [RelayCommand]
    private void Save()
    {
        var settings = _settingsService.Load();

        var conditions = new Dictionary<string, DeploymentConditionEntry>();
        var commands = new Dictionary<string, CommandOverrideEntry>();

        foreach (var row in Rows)
        {
            var architecture = DeploymentConditionRow.DisplayToArchitecture(row.ArchitectureDisplay);
            var network = DeploymentConditionRow.DisplayToNetwork(row.NetworkDisplay);
            var languages = DeploymentConditionsService.NormalizeCultures(
                row.LanguagesDisplay.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries));
            var split = row.VariantOptions.Contains(row.SplitDisplay)
                ? row.SplitDisplay : DeploymentConditionValues.NoSplit;
            var installMode = row.InstallModeCapable
                ? DeploymentConditionRow.DisplayToInstallMode(row.InstallModeDisplay) : "";

            // An app that asks for nothing stores no entry.
            if (architecture != DeploymentConditionValues.Any
                || network != DeploymentConditionValues.Any
                || languages.Length > 0
                || DeploymentConditionValues.IsSplit(split)
                || DeploymentConditionValues.IsInstallMode(installMode))
            {
                conditions[row.DefinitionId] = new DeploymentConditionEntry
                {
                    Architecture = architecture,
                    Languages = languages,
                    Network = network,
                    Split = split,
                    InstallMode = installMode
                };
            }

            var install = row.CommandInstall.Trim();
            var uninstall = row.CommandUninstall.Trim();
            if (install.Length > 0 || uninstall.Length > 0)
                commands[row.DefinitionId] = new CommandOverrideEntry { Install = install, Uninstall = uninstall };
        }

        settings.DeploymentConditions = conditions;
        settings.CommandOverrides = commands;
        _settingsService.Save(settings);

        SaveConditionTemplates();

        _snackbarService.Show("Deployment Conditions", "Selections saved.",
            Wpf.Ui.Controls.ControlAppearance.Success,
            new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Writes the template document only when a name or pattern actually
    /// changed, so an untouched page leaves the built-in defaults in place
    /// rather than materializing a file.
    /// </summary>
    private void SaveConditionTemplates()
    {
        var dirty = false;

        var arch = FindTemplate("cpu-arch");
        if (arch is not null && !string.IsNullOrWhiteSpace(ArchitectureConditionName)
            && arch.GlobalConditionName != ArchitectureConditionName.Trim())
        {
            arch.GlobalConditionName = ArchitectureConditionName.Trim();
            dirty = true;
        }

        var language = FindTemplate("os-language");
        if (language is not null && !string.IsNullOrWhiteSpace(LanguageConditionName)
            && language.GlobalConditionName != LanguageConditionName.Trim())
        {
            language.GlobalConditionName = LanguageConditionName.Trim();
            dirty = true;
        }

        var vpn = FindTemplate("vpn-connected");
        if (vpn is not null)
        {
            if (!string.IsNullOrWhiteSpace(VpnConditionName)
                && vpn.GlobalConditionName != VpnConditionName.Trim())
            {
                vpn.GlobalConditionName = VpnConditionName.Trim();
                dirty = true;
            }

            var patterns = VpnAdapterPatterns
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
            if (patterns.Length > 0 && !patterns.SequenceEqual(vpn.AdapterPatterns ?? []))
            {
                vpn.AdapterPatterns = patterns;
                dirty = true;
            }
        }

        if (dirty)
            _templateService.Save(_templates);
    }
}
