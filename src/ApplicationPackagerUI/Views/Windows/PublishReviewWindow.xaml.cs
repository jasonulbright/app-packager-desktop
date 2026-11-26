using System.Windows.Controls;
using System.Windows.Data;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class PublishReviewWindow
{
    public PublishReviewViewModel ViewModel { get; }

    public PublishReviewWindow(PublishReviewViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // One column for each system in use, in the order of the plan.
        for (var i = 0; i < viewModel.Plan.Systems.Count; i++)
        {
            Matrix.Columns.Add(PublishMatrixColumns.For(viewModel.Plan.Systems[i], i, 150));
        }
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => (viewModel.CanRun ? (System.Windows.UIElement)txtReviewComment : Matrix).Focus();
    }
}
