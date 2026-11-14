using System.Diagnostics;
using System.Windows;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>The WPF side of the publish actions: message boxes, pickers and the WSUS and One Click windows.</summary>
public sealed class PublishDialogs(IWsusAdminClient wsusClient, IChangePlanDialogs changePlans) : IPublishDialogs, IWsusPrompts
{
    /// <summary>The active window, so a prompt opened from a dialog stays in front of that dialog.</summary>
    private static Window? Owner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    /// <summary>A content dialog over the active window's own dialog host.</summary>
    private static async Task<ContentDialogResult> ShowAsync(string title, string message, string closeText, string? primaryText = null)
    {
        var host = Owner() as IDialogHostWindow ?? Application.Current?.MainWindow as IDialogHostWindow;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            DialogHostEx = host?.DialogHost
        };
        if (primaryText is not null) dialog.PrimaryButtonText = primaryText;
        return await dialog.ShowAsync(CancellationToken.None);
    }

    public void ShowMessage(string title, string message) => _ = ShowAsync(title, message, "OK");

    public async Task<bool> ConfirmAsync(string title, string message) =>
        await ShowAsync(title, message, "No", "Yes") == ContentDialogResult.Primary;

    public void ShowWsusUpdates(WsusSettings settings)
    {
        var viewModel = new WsusUpdatesViewModel(wsusClient, settings, this);
        var window = new WsusUpdatesWindow(viewModel, owner =>
        {
            var import = new WsusCatalogImportWindow(new WsusCatalogImportViewModel(wsusClient, settings, this)) { Owner = owner };
            import.ShowDialog();
        })
        { Owner = Owner() };
        window.ShowDialog();
    }

    public ChangePlanOutcome ShowChangePlan(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null) => changePlans.Show(plan, run, undo);

    public void ShowOneClickPlan(OneClickPlanViewModel viewModel)
    {
        var window = new OneClickPlanWindow(viewModel, PackagesViewModel.OneClickReportFolder) { Owner = Owner() };
        window.Show();
    }

    public string? PickPfxFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select the signing certificate PFX", Filter = "PFX files (*.pfx;*.p12)|*.pfx;*.p12" };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public Task<string?> AskPasswordAsync(string prompt)
    {
        var window = new WsusPasswordWindow(prompt) { Owner = Owner() };
        return Task.FromResult(window.ShowDialog() == true ? window.Password : null);
    }

    public string? PickCertificateSavePath(string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save the public signing certificate", Filter = "Certificate (*.cer)|*.cer", FileName = defaultFileName };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickIdFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select a text file of update IDs", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
