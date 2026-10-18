using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>The dialogs, clipboard and folder calls of the Detection Tester page, so its view model runs without a window.</summary>
public interface IDetectionTesterPrompts
{
    /// <summary>The chosen stage-manifest.json, or null when the operator cancels.</summary>
    string? PickManifest();

    Task<bool> ConfirmClearHistoryAsync(int count);

    void CopyText(string text);

    void OpenFolder(string folder);
}

public sealed class DetectionTesterPrompts(IContentDialogService dialogs) : IDetectionTesterPrompts
{
    public string? PickManifest()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import stage-manifest.json",
            Filter = "Stage manifest (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        return dialog.ShowDialog(DiscoveryPrompt.UsableOwner(Application.Current?.MainWindow)) == true ? dialog.FileName : null;
    }

    public async Task<bool> ConfirmClearHistoryAsync(int count)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear history",
            Content = count == 1 ? "Delete the 1 test in the history?" : $"Delete the {count} tests in the history?",
            PrimaryButtonText = "Clear history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    public void CopyText(string text)
    {
        // Another process can hold the clipboard open; the copy then fails without harm.
        try { Clipboard.SetText(text); }
        catch (ExternalException) { }
    }

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Reads and writes the page choices in settings.json without touching any other value.</summary>
public static class DetectionTesterPreferences
{
    public static DetectionTesterSettings? Load(SettingsService settings) => settings.Load().DetectionTester;

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, DetectionTesterSettings choices)
    {
        try
        {
            var stored = settings.Load();
            if (settings.IsFileUnreadable) return false;
            stored.DetectionTester = choices;
            settings.Save(stored);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

public static class DetectionTesterRegistration
{
    public static IServiceCollection AddDetectionTesterPage(this IServiceCollection services)
    {
        services.AddSingleton<IDetectionTesterPrompts, DetectionTesterPrompts>();
        services.AddSingleton<DetectionTesterViewModel>();
        services.AddSingleton<DetectionTesterPage>();
        return services;
    }
}
