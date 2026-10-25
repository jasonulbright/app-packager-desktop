using System.ComponentModel;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.ViewModels.Windows;
using Wpf.Ui.Controls;

namespace ApplicationPackagerUI.Views.Windows;

public partial class ChangePlanWindow
{
    private readonly ChangePlanViewModel _viewModel;

    public ChangePlanWindow(ChangePlanViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        btnConfirm.Appearance = viewModel.IsDestructive ? ControlAppearance.Danger : ControlAppearance.Primary;
        // The text box of a plan body takes its own rows, so the window grows to keep the list of changes readable inside a 1366x768 work area.
        if (viewModel.HasBody) Height = Math.Min(BodyHeight, System.Windows.SystemParameters.WorkArea.Height);
        viewModel.CloseRequested += (_, _) => Close();
        ContentRendered += (_, _) => FocusFirstControl();
        if (viewModel.HasBody)
        {
            txtBody.MaxHeight = WholeLines.BoxHeight(box: txtBody, lines: BodyLines);
            // The line count of the wrapped text changes with the width of the box.
            txtBody.SizeChanged += (_, _) => FitBody();
            Loaded += (_, _) => FitBody();
        }
        if (!viewModel.HasNotes) return;
        txtNotes.MaxHeight = WholeLines.BoxHeight(box: txtNotes, lines: OpenNotesLines);
        Loaded += (_, _) => OpenWithRoomForNotes();
        SizeChanged += (_, _) => FitNotes();
        gridPlan.SizeChanged += (_, _) => FitNotes();
        gridResult.SizeChanged += (_, _) => FitNotes();
    }

    public const double BodyHeight = 700;

    /// <summary>The most whole lines the PowerShell text box shows; more lines scroll.</summary>
    public const int BodyLines = 6;

    /// <summary>The least height of the change list: the column header and about three rows.</summary>
    public const double GridMinHeight = 120;

    /// <summary>The note lines of the notes box before the first layout; the layout then sets the lines the window has room for.</summary>
    public const int OpenNotesLines = 12;

    /// <summary>The fewest note lines the notes box shows when the window is short.</summary>
    public const int MinNotesLines = 2;

    /// <summary>The window opens inside a 1366x768 work area.</summary>
    public const double MaxOpenHeight = 728;

    private System.Windows.FrameworkElement VisibleGrid => gridResult.IsVisible ? gridResult : gridPlan;

    /// <summary>Sets the PowerShell text box to at most <see cref="BodyLines"/> whole lines, so no line shows cut and the scroll bar shows only when lines are left.</summary>
    private void FitBody()
    {
        if (!WholeLines.IsMeasured(txtBody))
        {
            WholeLines.WhenMeasured(box: txtBody, fit: FitBody);
            return;
        }
        var height = WholeLines.BoxHeight(box: txtBody, lines: BodyLines);
        if (Math.Abs(height - txtBody.MaxHeight) > 0.5) txtBody.MaxHeight = height;
    }

    /// <summary>
    /// Sets the notes box to the whole lines that fit while the change list keeps <see cref="GridMinHeight"/>, up to
    /// the lines of the notes, so no line shows cut and the scroll bar shows only when lines are left.
    /// </summary>
    private void FitNotes()
    {
        if (!WholeLines.IsMeasured(txtNotes))
        {
            WholeLines.WhenMeasured(box: txtNotes, fit: FitNotes);
            return;
        }
        var line = WholeLines.LineHeight(txtNotes);
        var lines = Math.Max(MinNotesLines, WholeLines.LineCount(txtNotes));
        var shown = WholeLines.VisibleLines(txtNotes);
        var spare = (int)Math.Floor((VisibleGrid.ActualHeight - GridMinHeight) / line);
        var height = WholeLines.BoxHeight(box: txtNotes, lines: Math.Clamp(shown + spare, MinNotesLines, lines));
        if (Math.Abs(height - txtNotes.MaxHeight) > 0.5) txtNotes.MaxHeight = height;
    }

    /// <summary>
    /// When the notes need more lines than the opened window has room for, the window grows by those lines, up to the
    /// work area and <see cref="MaxOpenHeight"/>, and stays centered on its first position.
    /// </summary>
    private void OpenWithRoomForNotes()
    {
        UpdateLayout();
        FitNotes();
        UpdateLayout();
        var hidden = txtNotes.ExtentHeight - txtNotes.ViewportHeight;
        if (hidden < 1) return;
        var area = System.Windows.SystemParameters.WorkArea;
        var limit = Math.Min(area.Height, MaxOpenHeight);
        var line = WholeLines.LineHeight(txtNotes);
        var grow = Math.Min(Math.Ceiling(hidden / line) * line, limit - ActualHeight);
        if (grow < line) return;
        var top = Top;
        Height = ActualHeight + grow;
        if (!double.IsNaN(top)) Top = Math.Max(area.Top, Math.Min(top - grow / 2, area.Bottom - Height));
    }

    /// <summary>Puts the focus on the first plan line, or on the first confirmation box or button when the plan has no line.</summary>
    public void FocusFirstControl()
    {
        if (Helpers.DataGridFocus.FocusFirstCell(gridPlan)) return;
        foreach (var control in new System.Windows.Controls.Control[] { chkDestructive, chkExtra, chkSecond, chkOption, btnConfirm, btnCancel })
            if (control.IsVisible && control.IsEnabled && control.Focus()) return;
    }

    // Closing the window while a run is in progress would hide its result; the close turns
    // into a cancel request and the window stays open until the run reports or the operator
    // chooses to close without waiting.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.CanClose)
        {
            e.Cancel = true;
            _viewModel.RequestCancel();
        }
        base.OnClosing(e);
    }
}
