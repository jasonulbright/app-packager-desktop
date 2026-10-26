using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class HygieneImportWindow
{
    /// <summary>The most plan lines the box shows; more lines scroll.</summary>
    public const int PlanLines = 12;

    public HygieneImportWindow(HygieneImportViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        ContentRendered += (_, _) => txtImportPlan.Focus();
        txtImportPlan.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) FitPlan();
        };
        Loaded += (_, _) => FitPlan();
    }

    /// <summary>The box shows every line of the plan up to <see cref="PlanLines"/> whole lines, from the laid-out line height; more lines scroll.</summary>
    private void FitPlan()
    {
        if (!WholeLines.IsMeasured(txtImportPlan))
        {
            WholeLines.WhenMeasured(box: txtImportPlan, fit: FitPlan);
            return;
        }
        var height = WholeLines.BoxHeight(box: txtImportPlan, lines: Math.Min(PlanLines, WholeLines.LineCount(txtImportPlan)));
        if (Math.Abs(height - txtImportPlan.MaxHeight) > 0.5) txtImportPlan.MaxHeight = height;
    }
}