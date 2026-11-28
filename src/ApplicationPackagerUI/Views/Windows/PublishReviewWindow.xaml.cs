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
        Controls.Mock.DialogFrame.Attach(this);
        // The sizes of the two windows of the mock: one system, or several with a column each.
        Width = viewModel.SingleSystem ? 1190 : 1390;
        Height = viewModel.SingleSystem ? 540 : 575;
        Controls.Mock.DialogFrame.RoundAs(this, Height);
        Controls.Mock.DialogFrame.KeepInWorkArea(this, BodyScroll);
        // The glyphs of the foot, and in the window for one system also its labels, round one pixel lower in the mock.
        foreach (var button in new[] { btnReviewBack, btnReviewConfirm })
        {
            Controls.Mock.Mk.SetIconDrop(button, 1);
            Controls.Mock.Mk.SetTextDrop(button, viewModel.SingleSystem ? 1 : 0);
        }
        // One column for each system in use, in the order of the plan, with the widths of the mock.
        double[] widths = [353, 242.3, 332.6];
        for (var i = 0; i < viewModel.Plan.Systems.Count; i++)
        {
            var system = viewModel.Plan.Systems[i];
            Matrix.Columns.Add(PublishMatrixColumns.Text(viewModel.SystemHeader(system), i, widths[Math.Min(i, widths.Length - 1)], 150, (System.Windows.Style)FindResource("MatrixCell")));
        }
        // The glyphs of the banner and of Preview stand one pixel higher in the window for several systems.
        if (!viewModel.SingleSystem)
        {
            Controls.Mock.Mk.SetDrop(icoBanner, -1);
            Controls.Mock.Mk.SetDrop(icoPreview, -1);
        }
        // A row that changed since the confirmed review, or that the view of the grid hides, says so in its own column. A review with no
        // such row is the table of the mock.
        if (viewModel.HasCheckedMarks)
        {
            Matrix.Columns.Insert(2, CheckedColumn());
            LineGrid.Columns.Insert(2, CheckedColumn());
        }
        Matrix.LoadingRow += (_, e) =>
        {
            var index = e.Row.GetIndex();
            e.Row.Resources["CellShift"] = new System.Windows.Media.TranslateTransform(0, index == 0 ? -1 : 0);
            e.Row.Resources["NameShift"] = new System.Windows.Media.TranslateTransform(0, index == 1 ? -1 : 0);
        };
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => (viewModel.CanRun ? (System.Windows.UIElement)btnReviewConfirm : btnReviewBack).Focus();
    }

    private DataGridTextColumn CheckedColumn() => new()
    {
        Header = "Checked", Binding = new Binding("Checked"), Width = new DataGridLength(130, DataGridLengthUnitType.Star), MinWidth = 110,
        ElementStyle = (System.Windows.Style)FindResource("Mk.CellText")
    };
}
