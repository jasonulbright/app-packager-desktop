using System.Windows;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>The Old Application window: search the site, link or unlink the old application of a line. The confirmation opens over this window.</summary>
public partial class OldApplicationWindow : ApplicationPackagerUI.Services.IDialogHostWindow
{
    public ContentDialogHost DialogHost => PromptHost;

    public OldApplicationWindow(OldApplicationViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => txtSearch.Focus();
    }

    /// <summary>The confirmation of a write: the text first, the action as the primary button, Cancel as the default.</summary>
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            PrimaryButtonText = title,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            DialogHostEx = PromptHost
        };
        return await dialog.ShowAsync(CancellationToken.None) == ContentDialogResult.Primary;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
