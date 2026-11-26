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
            Matrix.Columns.Add(new DataGridTextColumn
            {
                Header = viewModel.Plan.Systems[i],
                Binding = new Binding($"Cells[{i}]"),
                ElementStyle = WrapStyle(),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 180
            });
        }
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => (viewModel.CanRun ? (System.Windows.UIElement)txtReviewComment : Matrix).Focus();
    }

    private static System.Windows.Style WrapStyle()
    {
        var style = new System.Windows.Style(typeof(System.Windows.Controls.TextBlock));
        style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.TextBlock.TextWrappingProperty, System.Windows.TextWrapping.Wrap));
        return style;
    }
}
