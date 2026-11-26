using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Services.OneClick;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>The earlier One Click runs, newest first, from the report files (overhaul V1, phase 6).</summary>
public partial class OneClickHistoryWindow
{
    private readonly string _folder;

    public OneClickHistoryWindow(string folder)
    {
        _folder = folder;
        InitializeComponent();
        IReadOnlyList<OneClickReportSummary> runs = [];
        var failure = "";
        try { runs = OneClickReportWriter.List(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex.Message; }
        gridHistory.ItemsSource = runs;
        txtHistorySummary.Text = failure.Length > 0 ? "The report folder cannot be read: " + failure
            : runs.Count == 0 ? "No One Click run wrote a report yet."
            : $"{runs.Count} {(runs.Count == 1 ? "run" : "runs")}, newest first.";
    }

    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        btnHistoryOpen.IsEnabled = gridHistory.SelectedItem is OneClickReportSummary;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (gridHistory.SelectedItem is not OneClickReportSummary run || !File.Exists(run.MarkdownPath)) return;
        try { Process.Start(new ProcessStartInfo(run.MarkdownPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\""));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
