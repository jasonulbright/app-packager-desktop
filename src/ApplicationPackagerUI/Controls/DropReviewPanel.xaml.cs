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

    public DropReviewPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the acknowledgment toggles, so the host can re-gate its Stage button.</summary>
    public event EventHandler? ConfirmationChanged;

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
        _rows.Add(ProposedFieldRow.From(AppNameLabel, analysis.AppName));
        _rows.Add(ProposedFieldRow.From(PublisherLabel, analysis.Publisher));
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
    private void UpdateConfirmHint() =>
        ConfirmHintText.Text = IsConfirmed
            ? "Confirmed. Stage and Save as definition are available."
            : "Stage and Save as definition appear once this is confirmed. Staging a wrong silent switch "
              + "produces a package that fails on every target device.";

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
