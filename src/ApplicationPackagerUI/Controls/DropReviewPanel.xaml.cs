using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Controls;

/// <summary>
/// Editable review of one dropped-installer analysis. Every proposed value carries its
/// confidence badge and provenance; a predicted analysis stays ungated until the operator
/// ticks the acknowledgment, which is the flag the engine's stage-manifest build requires.
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

    public void Load(DroppedInstallerAnalysis analysis)
    {
        Analysis = analysis;

        FileNameText.Text = analysis.FileName;
        DetectedText.Text =
            $"Detected: {analysis.Engine} ({analysis.InstallerType})   Architecture: " +
            $"{(string.IsNullOrEmpty(analysis.Architecture) ? "unknown" : analysis.Architecture)}   " +
            $"Confidence: {analysis.Confidence}";

        _rows.Clear();
        _rows.Add(ProposedFieldRow.From(AppNameLabel, analysis.AppName));
        _rows.Add(ProposedFieldRow.From(PublisherLabel, analysis.Publisher));
        _rows.Add(ProposedFieldRow.From(VersionLabel, analysis.SoftwareVersion));
        _rows.Add(ProposedFieldRow.From(InstallArgsLabel, analysis.InstallArgs));
        _rows.Add(ProposedFieldRow.From(UninstallArgsLabel, analysis.UninstallArgs));
        _rows.Add(ProposedFieldRow.From(UninstallCommandLabel, analysis.UninstallCommand));
        _rows.Add(new ProposedFieldRow
        {
            Label = DetectionLabel,
            Value = DescribeDetection(analysis),
            Confidence = analysis.UninstallRegistryKey.Confidence,
            Provenance = string.IsNullOrWhiteSpace(analysis.UninstallRegistryKey.Source)
                ? analysis.DetectionHint
                : $"{analysis.UninstallRegistryKey.Source} — {analysis.DetectionHint}",
            IsEditable = false
        });

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
        UninstallCommand = Read(UninstallCommandLabel)
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

    private static string DescribeDetection(DroppedInstallerAnalysis analysis)
    {
        var detection = analysis.Detection;
        if (detection.Type == "MSIX")
            return $"MSIX package family: {detection.FileName}";

        var key = string.IsNullOrWhiteSpace(detection.RegistryKeyRelative)
            ? "uninstall key derived from the application name at stage time"
            : detection.RegistryKeyRelative;
        var view = detection.Is64Bit ? "64-bit view" : "32-bit view";
        return $"Registry {detection.Type}: {key} — {detection.ValueName} {detection.Operator} version ({view})";
    }

    private void ConfirmCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateConfirmHint();
        ConfirmationChanged?.Invoke(this, EventArgs.Empty);
    }
}
