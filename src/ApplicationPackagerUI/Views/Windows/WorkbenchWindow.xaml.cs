using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using Microsoft.Win32;

namespace ApplicationPackagerUI.Views.Windows;

public partial class WorkbenchWindow : IWorkbenchPrompts
{
    private readonly WorkbenchWindowViewModel _viewModel;
    private bool _closeConfirmed;

    public WorkbenchWindow(WorkbenchWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.Prompts = this;
        viewModel.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The application to open first; null opens the first one listed.</summary>
    public string? PreselectApplicationId { get; set; }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await _viewModel.InitializeAsync(PreselectApplicationId);
    }

    /// <summary>
    /// Closing cannot wait for a dialog, so a dirty close is cancelled, the prompt runs,
    /// and the window closes again once Save or Discard allowed it.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.IsDirty) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(async () =>
        {
            if (!await _viewModel.ConfirmDiscardAsync("This profile has unsaved changes. Save them before closing?")) return;
            _closeConfirmed = true;
            Close();
        });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            pnlFind.Visibility = Visibility.Visible;
            txtFind.Focus();
            e.Handled = true;
        }
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FindNext();
        e.Handled = true;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private void FindClose_Click(object sender, RoutedEventArgs e) => pnlFind.Visibility = Visibility.Collapsed;

    private void FindNext()
    {
        var needle = txtFind.Text;
        if (string.IsNullOrEmpty(needle)) return;
        var box = pnlDetectionScript.IsVisible ? txtDetectScript : txtScript;
        var text = box.Text ?? "";
        var start = box.SelectionStart + Math.Max(box.SelectionLength, 1);
        if (start >= text.Length) start = 0;
        var index = text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;
        box.Focus();
        box.Select(index, needle.Length);
        box.ScrollToLine(Math.Max(0, box.GetLineIndexFromCharacterIndex(index)));
    }

    private void Script_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        txtScriptGutter.ScrollToVerticalOffset(e.VerticalOffset);

    private void DetectScript_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        txtDetectGutter.ScrollToVerticalOffset(e.VerticalOffset);

    private void SiteCondition_LostFocus(object sender, RoutedEventArgs e) => _viewModel.SaveSiteConditions();

    private void ExportBundle_Click(object sender, RoutedEventArgs e) => _viewModel.ExportBundleCommand.Execute(null);

    private void ImportBundle_Click(object sender, RoutedEventArgs e) => _viewModel.ImportBundleCommand.Execute(null);

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        dialog.DialogHostEx = WorkbenchDialogHost;
        return await dialog.ShowAsync(CancellationToken.None);
    }

    public async Task<WorkbenchUnsavedAnswer> UnsavedAsync(string message)
    {
        var result = await ShowAsync(new ContentDialog
        {
            Title = "Unsaved Changes",
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        });
        return result switch
        {
            ContentDialogResult.Primary => WorkbenchUnsavedAnswer.Save,
            ContentDialogResult.Secondary => WorkbenchUnsavedAnswer.Discard,
            _ => WorkbenchUnsavedAnswer.Cancel
        };
    }

    public async Task<string?> NameAsync(string prompt, string value)
    {
        var box = new Wpf.Ui.Controls.TextBox { Text = value, MinWidth = 360, ClearButtonEnabled = false };
        var panel = new StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(box);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        var result = await ShowAsync(new ContentDialog
        {
            Title = "Profile",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        });
        var name = box.Text?.Trim();
        return result == ContentDialogResult.Primary && !string.IsNullOrEmpty(name) ? name : null;
    }

    public async Task<bool> YesNoAsync(string title, string message)
    {
        var result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Primary
        });
        return result == ContentDialogResult.Primary;
    }

    public async Task InfoAsync(string title, string message)
    {
        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        });
    }

    public IReadOnlyList<string> PickFiles(string title, string filter, bool multiselect)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = multiselect };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public async Task<ByoReplacementReview?> ReviewReplacementAsync(
        ApplicationPackagerEngine.Services.DroppedInstallerAnalysis analysis,
        IReadOnlyList<ApplicationPackagerEngine.Models.Workbench.ByoIdentityChange> changes,
        int revision, int previousRevision, string displayName, string publisher)
    {
        var panel = new Controls.ByoReplacementPanel();
        panel.Load(analysis, changes, revision, previousRevision, displayName, publisher);
        var review = panel.ReviewPanel;
        var dialog = new ContentDialog
        {
            Title = "Replace Installer",
            Content = panel,
            PrimaryButtonText = "Add revision",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = review.IsConfirmed,
            DialogMaxWidth = 720
        };
        review.ConfirmationChanged += (_, _) => dialog.IsPrimaryButtonEnabled = review.IsConfirmed;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        var problem = review.Validate();
        if (problem is not null)
        {
            await InfoAsync("Missing Value", problem);
            return null;
        }
        return new ByoReplacementReview(review.ReadOverrides(), review.IsConfirmed);
    }
}
