using System.Windows;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class AddFilePage
{
    public AddFilePage(AddFileViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        var viewModel = (AddFileViewModel)DataContext;
        // The OLE drop source blocks until the handler returns; the analysis starts after it.
        Dispatcher.BeginInvoke(() => viewModel.DropFiles(paths));
    }
}
