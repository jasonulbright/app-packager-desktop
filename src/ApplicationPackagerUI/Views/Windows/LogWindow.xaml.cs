using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>The log of the session: the newest lines, which follow the end while the view is at the end.</summary>
public partial class LogWindow
{
    private readonly OperationLog _log;

    public LogWindow(PackagesViewModel viewModel)
    {
        InitializeComponent();
        _log = viewModel.LogLines;
        lstLog.ItemsSource = _log;
        txtLogNote.Text = $"The newest {_log.Limit} lines. The log file holds every line: {PackagesViewModel.SessionLogFile}";
        _log.CollectionChanged += OnLines;
        Closed += (_, _) => _log.CollectionChanged -= OnLines;
        Loaded += (_, _) =>
        {
            ScrollToEnd();
            lstLog.Focus();
        };
    }

    private void OnLines(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The layout has not run for the new line yet, so the position is the one before it.
        if (e.Action != NotifyCollectionChangedAction.Add || !AtEnd()) return;
        Dispatcher.BeginInvoke(ScrollToEnd);
    }

    private bool AtEnd() => Scroller(lstLog) is not { } scroller || scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5;

    private static ScrollViewer? Scroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as ScrollViewer ?? Scroller(child)) is { } found) return found;
        }
        return null;
    }

    private void ScrollToEnd()
    {
        if (_log.Count > 0) lstLog.ScrollIntoView(_log[^1]);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var lines = lstLog.SelectedItems.Count > 0 ? lstLog.SelectedItems.Cast<LogEntry>() : _log;
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(PackagesViewModel.SessionLogFile)!;
        if (!Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", folder)); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
