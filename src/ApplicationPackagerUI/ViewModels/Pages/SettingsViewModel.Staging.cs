using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// Settings, Folders: the check of the download root when it is set, typed or chosen. A folder that other accounts can write to gets an inline warning,
/// a safe-folder action and the explicit "Allow this folder anyway" control. The warning never blocks saving; the stage still stops at the folder.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>The warning of the folder that blocks staging, the same text as the stage result and the run status. Empty when none applies.</summary>
    [ObservableProperty] private string _stagingWarning = "";

    /// <summary>Who allowed the folder and when, when it is used only because of the acknowledgement. Empty otherwise.</summary>
    [ObservableProperty] private string _stagingAllowedText = "";

    public bool ShowStagingWarning => StagingWarning.Length > 0;
    public bool ShowStagingAllowed => StagingAllowedText.Length > 0;

    partial void OnStagingWarningChanged(string value) => OnPropertyChanged(nameof(ShowStagingWarning));
    partial void OnStagingAllowedTextChanged(string value) => OnPropertyChanged(nameof(ShowStagingAllowed));
    partial void OnDownloadRootChanged(string value) => RefreshStagingStatus();

    /// <summary>The window that asks for the acknowledgement. A test replaces it.</summary>
    public IStagingAllowancePrompt StagingPrompt { get; set; } = new ApplicationPackagerUI.Views.Windows.StagingAllowancePrompt();

    /// <summary>Checks the folder in the box now and sets the warning or the allowed text.</summary>
    public void RefreshStagingStatus()
    {
        var status = StagingRootGuard.Check(DownloadRoot);
        StagingWarning = status.State switch
        {
            StagingRootGuard.FolderState.Blocked or StagingRootGuard.FolderState.Unreadable => status.Text,
            _ => ""
        };
        StagingAllowedText = status.State == StagingRootGuard.FolderState.Allowed
            ? $"Staging in this folder is allowed by acknowledgement ({status.Allowance!.User}, {status.Allowance.AcknowledgedAt.LocalDateTime:yyyy-MM-dd HH:mm}). The allowance ends when the folder or its access list changes."
            : "";
    }

    /// <summary>Creates the folder that only the current user and administrators can write and puts it in the box. Save Settings keeps it.</summary>
    [RelayCommand]
    private void CreateSafeStagingFolder()
    {
        try
        {
            DownloadRoot = StagingRootGuard.Prepare(StagingRootGuard.SafeFolder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            _snackbarService.Show("Staging folder", "The safe folder could not be made. " + ex.Message, Wpf.Ui.Controls.ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.ErrorCircle24), TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>Opens the acknowledgement. Only a checked box and the Accept button store the allowance of this folder.</summary>
    [RelayCommand]
    private void AllowStagingFolder()
    {
        var status = StagingRootGuard.Check(DownloadRoot);
        if (status.State != StagingRootGuard.FolderState.Blocked) return;
        if (!StagingPrompt.Confirm(status.Folder, StagingRootGuard.AllowanceReason)) return;
        try
        {
            StagingAllowanceStore.Allow(status.Folder, acknowledged: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _snackbarService.Show("Staging folder", "The allowance was not stored. " + ex.Message, Wpf.Ui.Controls.ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.ErrorCircle24), TimeSpan.FromSeconds(6));
        }

        RefreshStagingStatus();
    }

    /// <summary>Ends the allowance of the folder in the box; staging there is blocked again.</summary>
    [RelayCommand]
    private void RemoveStagingAllowance()
    {
        StagingAllowanceStore.Revoke(DownloadRoot);
        RefreshStagingStatus();
    }
}
