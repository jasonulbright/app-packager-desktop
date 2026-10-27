using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.InstallerScripts;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Controls;

/// <summary>
/// Editable review of one dropped-installer analysis. Every proposed value carries its
/// confidence badge and provenance; a predicted analysis stays ungated until the operator
/// ticks the acknowledgment, which is the flag the engine's stage-manifest build requires.
/// A switchable installer adds an Install for choice whose branch rewrites the argument,
/// uninstall and detection rows together.
/// </summary>
public partial class DropReviewPanel : UserControl
{
    private const string AppNameLabel = "Application";
    private const string PublisherLabel = "Publisher";
    private const string VersionLabel = "Version";
    private const string InstallArgsLabel = "Install args";
    private const string UninstallArgsLabel = "Uninstall args";
    private const string UninstallCommandLabel = "Uninstall command";
    private const string DetectionLabel = "Detection";

    private readonly List<ProposedFieldRow> _rows = [];
    private bool _loadingMode;
    private (string Name, string Publisher)? _keptIdentity;

    public DropReviewPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Raised when the acknowledgment toggles, so the host can re-gate its Stage button.</summary>
    public event EventHandler? ConfirmationChanged;

    /// <summary>Raised by Save as Application; the host closes its dialog and persists the drop.</summary>
    public event EventHandler? SaveAsApplicationRequested;

    /// <summary>False hides Save as Application, for a review that is not a fresh drop.</summary>
    public bool ShowSaveAsApplication
    {
        get => SaveApplicationRow.Visibility == Visibility.Visible;
        set => SaveApplicationRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public DroppedInstallerAnalysis? Analysis { get; private set; }

    /// <summary>True when at least one proposed field is a prediction.</summary>
    public bool RequiresConfirmation { get; private set; }

    public bool IsConfirmed => !RequiresConfirmation || ConfirmCheckBox.IsChecked == true;

    /// <summary>AllUsers or CurrentUser as chosen in the Install for box; null when the installer offers no choice.</summary>
    public string? SelectedInstallMode =>
        InstallModeRow.Visibility == Visibility.Visible && InstallModeBox.SelectedItem is ComboBoxItem item
            ? item.Tag as string
            : null;

    public void Load(DroppedInstallerAnalysis analysis)
    {
        Analysis = analysis;

        FileNameText.Text = analysis.FileName;
        DetectedText.Text =
            $"Detected: {analysis.Engine} ({analysis.InstallerType})   Architecture: " +
            $"{(string.IsNullOrEmpty(analysis.Architecture) ? "unknown" : analysis.Architecture)}   " +
            $"Confidence: {analysis.Confidence}";

        _loadingMode = true;
        InstallModeRow.Visibility = analysis.HasInstallModeChoice ? Visibility.Visible : Visibility.Collapsed;
        if (analysis.HasInstallModeChoice)
        {
            InstallModeBox.SelectedIndex = analysis.DefaultInstallMode == "CurrentUser" ? 1 : 0;
            InstallModeHint.Text = $"The installer defaults to {(analysis.DefaultInstallMode == "CurrentUser" ? "User" : "System")}.";
        }
        _loadingMode = false;

        BuildRows(analysis, branch: null);
    }

    private void BuildRows(DroppedInstallerAnalysis analysis, InstallBranch? branch)
    {
        var installArgs = branch is null ? analysis.InstallArgs : ProposedField.Confirmed(branch.InstallArgs, analysis.InstallArgs.Source);
        var uninstallCommand = branch is null ? analysis.UninstallCommand : ProposedField.Confirmed(branch.SilentUninstallCommand, analysis.UninstallCommand.Source);

        _rows.Clear();
        _rows.Add(_keptIdentity is { } kept ? KeptRow(AppNameLabel, kept.Name) : ProposedFieldRow.From(AppNameLabel, analysis.AppName));
        _rows.Add(_keptIdentity is { } keptPublisher ? KeptRow(PublisherLabel, keptPublisher.Publisher) : ProposedFieldRow.From(PublisherLabel, analysis.Publisher));
        _rows.Add(ProposedFieldRow.From(VersionLabel, analysis.SoftwareVersion));
        _rows.Add(ProposedFieldRow.From(InstallArgsLabel, installArgs));
        _rows.Add(ProposedFieldRow.From(UninstallArgsLabel, analysis.UninstallArgs));
        _rows.Add(ProposedFieldRow.From(UninstallCommandLabel, uninstallCommand));
        _rows.Add(new ProposedFieldRow
        {
            Label = DetectionLabel,
            Value = DescribeDetection(analysis, branch),
            Confidence = analysis.UninstallRegistryKey.Confidence,
            Provenance = string.IsNullOrWhiteSpace(analysis.UninstallRegistryKey.Source)
                ? analysis.DetectionHint
                : $"{analysis.UninstallRegistryKey.Source} — {analysis.DetectionHint}",
            IsEditable = false
        });

        FieldList.ItemsSource = null;
        FieldList.ItemsSource = _rows;

        RequiresConfirmation = _rows.Any(r => r.IsPredicted);
        var gateVisibility = RequiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
        ConfirmCheckBox.Visibility = gateVisibility;
        ConfirmHintText.Visibility = gateVisibility;
        ConfirmCheckBox.IsChecked = false;
        UpdateConfirmHint();
    }

    /// <summary>
    /// The dialog host hides a disabled footer button rather than greying it, so the gate is
    /// stated in the panel: without it the Stage button simply disappears with no reason given.
    /// </summary>
    private void UpdateConfirmHint()
    {
        ConfirmHintText.Text = IsConfirmed
            ? _gatedActionsConfirmed
            : _gatedActionsPending;
        SaveApplicationButton.IsEnabled = IsConfirmed;
    }

    private string _gatedActionsConfirmed = "Confirmed. Stage and Save as definition are available.";
    private string _gatedActionsPending = "Stage and Save as definition appear once this is confirmed.";

    /// <summary>Names the host's gated buttons in the confirmation hint.</summary>
    public void SetGatedActionsText(string confirmed, string pending)
    {
        _gatedActionsConfirmed = confirmed;
        _gatedActionsPending = pending;
        UpdateConfirmHint();
    }

    /// <summary>
    /// Shows the name and publisher an existing application keeps, read-only, in place of the
    /// analysed values: a replacement installer adds a revision and does not rename the application.
    /// </summary>
    public void KeepIdentity(string name, string publisher)
    {
        _keptIdentity = (name, publisher);
        if (Analysis is null) return;
        var mode = SelectedInstallMode;
        var branch = mode is not null && mode != Analysis.DefaultInstallMode && Analysis.ModeBranches.TryGetValue(mode, out var selected)
            ? selected
            : null;
        BuildRows(Analysis, branch);
    }

    private static ProposedFieldRow KeptRow(string label, string value) => new()
    {
        Label = label,
        Value = value,
        Confidence = AnalysisConfidence.Confirmed,
        Provenance = "kept by the application",
        IsEditable = false
    };

    private void SaveApplicationButton_Click(object sender, RoutedEventArgs e) =>
        SaveAsApplicationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Operator edits, in the shape the engine's stage/definition builders accept.</summary>
    public DroppedInstallerOverrides ReadOverrides() => new()
    {
        AppName = Read(AppNameLabel),
        Publisher = Read(PublisherLabel),
        SoftwareVersion = Read(VersionLabel),
        InstallArgs = Read(InstallArgsLabel),
        UninstallArgs = Read(UninstallArgsLabel),
        UninstallCommand = Read(UninstallCommandLabel),
        InstallMode = SelectedInstallMode
    };

    /// <summary>Returns the first missing required value, or null when the edits are complete.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Read(AppNameLabel)))
            return "Application name is required.";
        if (string.IsNullOrWhiteSpace(Read(VersionLabel)))
            return "Version is required.";
        if (Analysis is not null && Analysis.InstallerType != "MSI" && string.IsNullOrWhiteSpace(Read(InstallArgsLabel)))
            return "Install args are required for a non-MSI installer.";
        return null;
    }

    private string Read(string label) =>
        (_rows.FirstOrDefault(r => r.Label == label)?.Value ?? "").Trim();

    private static string DescribeDetection(DroppedInstallerAnalysis analysis, InstallBranch? branch)
    {
        var detection = analysis.Detection;
        if (detection.Type == "MSIX")
            return $"MSIX package family: {detection.FileName}";

        if (branch is { UninstallRegistryKey: { Length: > 0 } })
        {
            var branchView = branch.RegistryHive == "HKLM" ? (branch.Is64BitView ? "64-bit view" : "32-bit view") : "user hive";
            return $"Registry {detection.Type}: {branch.UninstallRegistryKey} — {detection.ValueName} {detection.Operator} version ({branchView})";
        }

        var key = string.IsNullOrWhiteSpace(detection.RegistryKeyRelative)
            ? "uninstall key derived from the application name at stage time"
            : (detection.IsCurrentUserHive ? @"HKCU:\" : @"HKLM:\") + detection.RegistryKeyRelative;
        var view = detection.IsCurrentUserHive ? "user hive" : detection.Is64Bit ? "64-bit view" : "32-bit view";
        return $"Registry {detection.Type}: {key} — {detection.ValueName} {detection.Operator} version ({view})";
    }

    /// <summary>The fields keep at least this height, so a very small host scrolls the whole dialog instead of the fields alone.</summary>
    public const double MinFieldsHeight = 120;

    /// <summary>
    /// True caps the fields to the room the dialog gives its content, so the rows below them stay in view.
    /// False lets the fields take their full height inside a host that scrolls.
    /// </summary>
    public bool FitToWindow { get; set; }

    private Window? _fitWindow;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!FitToWindow || Window.GetWindow(this) is not { } window) return;
        _fitWindow = window;
        window.SizeChanged += OnWindowSizeChanged;
        QueueFit();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_fitWindow is not null) _fitWindow.SizeChanged -= OnWindowSizeChanged;
        _fitWindow = null;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => QueueFit();

    private void QueueFit() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, FitFields);

    /// <summary>
    /// Lifts the cap, lets the layout run, and takes from the fields exactly the height that the scroll viewer of
    /// the dialog reports as overflow. The result follows the real chrome of the dialog, not an assumed size.
    /// </summary>
    private void FitFields()
    {
        if (!IsLoaded || !FitToWindow) return;
        FieldsScroller.MaxHeight = double.PositiveInfinity;
        UpdateLayout();
        var outer = OuterScroller();
        if (outer is null || outer.ScrollableHeight < 0.5) return;
        FieldsScroller.MaxHeight = Math.Max(MinFieldsHeight, FieldsScroller.ActualHeight - outer.ScrollableHeight);
    }

    /// <summary>The nearest scroll viewer above the panel: the one the dialog puts around its content.</summary>
    private ScrollViewer? OuterScroller()
    {
        for (DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(this); parent is not null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }
    private void InstallModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMode || Analysis is null || !Analysis.HasInstallModeChoice) return;
        var mode = SelectedInstallMode;
        InstallBranch? branch = null;
        if (mode is not null && mode != Analysis.DefaultInstallMode && Analysis.ModeBranches.TryGetValue(mode, out var selected))
            branch = selected;
        BuildRows(Analysis, branch);
        ConfirmationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ConfirmCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateConfirmHint();
        ConfirmationChanged?.Invoke(this, EventArgs.Empty);
    }
}
