using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerUI.Controls;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Brush = System.Windows.Media.Brush;
using TextBlock = System.Windows.Controls.TextBlock;
using Window = System.Windows.Window;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Replace Installer dialog in a real content dialog: no label touches the border of the dialog or of its input.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ByoReplacementDialogRenderTests
{
    /// <summary>The window the main window gives its page: the full size less the closed pane and the frame.</summary>
    private const double Frame = 48;

    /// <summary>The least clear space between a label and the edge it sits next to.</summary>
    private const double MinimumGap = 6;

    public static TheoryData<ApplicationTheme, int, int> Sizes => new()
    {
        { ApplicationTheme.Light, 1440, 900 },
        { ApplicationTheme.Dark, 1440, 900 },
        { ApplicationTheme.Light, 1024, 720 }
    };

    private static void WithDialog(ApplicationTheme theme, int width, int height, Action<Window, ContentDialog, ByoReplacementPanel> test)
    {
        WithApplication(theme, () =>
        {
            var host = new ContentDialogHost();
            var window = new Window
            {
                Width = width - Frame, Height = height - Frame, WindowStyle = WindowStyle.None,
                Background = (Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush"),
                Content = new Grid { Children = { host } }
            };
            Show(window);
            var service = new ContentDialogService();
            service.SetDialogHost(host);
            var panel = new ByoReplacementPanel();
            panel.Load(DropReviewDialogRenderTests.Analysis(),
                [new ByoIdentityChange("Architecture", "x64", "x86", ByoChangeSeverity.Blocking), new ByoIdentityChange("Publisher", "Contoso", "Contoso Ltd", ByoChangeSeverity.Review)],
                2, 1, "Contoso Application", "Contoso");
            var dialog = new ContentDialog
            {
                Title = "Replace Installer", Content = panel, PrimaryButtonText = "Add revision", CloseButtonText = "Cancel", DialogMaxWidth = 720
            };
            var shown = service.ShowAsync(dialog, CancellationToken.None);
            try
            {
                Settle(window);
                StaTestHelper.PumpUntil(() => panel.IsLoaded);
                Settle(window);
                test(window, dialog, panel);
            }
            finally
            {
                dialog.Hide(ContentDialogResult.None);
                StaTestHelper.PumpUntil(() => shown.IsCompleted);
                window.Close();
            }
        });
    }

    [WindowTheory]
    [MemberData(nameof(Sizes))]
    public void NoLabelOfTheReplaceDialogTouchesAnEdge(ApplicationTheme theme, int width, int height)
    {
        WithDialog(theme, width, height, (window, dialog, panel) =>
        {
            var where = $"Replace Installer {theme} {width}x{height}";

            Pixels(window, panel, $"byo-replace-{theme}-{width}x{height}");
            var scroller = (ScrollViewer)panel.FindName("Scroller");
            var review = panel.ReviewPanel;
            var list = (ItemsControl)review.FindName("FieldList");

            // A list that takes the focus draws a focus box around all its rows, and the labels sit on that box.
            Assert.False(list.Focusable, $"{where}: the field list takes the focus.");
            Assert.False(System.Windows.Input.Keyboard.FocusedElement is ItemsControl, $"{where}: an items control holds the focus.");
            var rows = Descendants(list).OfType<ContentPresenter>().Where(p => p.Content is ApplicationPackagerUI.Models.ProposedFieldRow).ToList();
            Assert.True(rows.Count > 0, $"{where}: no field row was found.");
            foreach (var row in rows)
            {
                var name = ((ApplicationPackagerUI.Models.ProposedFieldRow)row.Content).Label;
                var label = Descendants(row).OfType<TextBlock>().First(t => t.Text == name);
                var input = Descendants(row).OfType<Wpf.Ui.Controls.TextBox>().First();
                var labelBox = label.TransformToAncestor(window).TransformBounds(new Rect(label.RenderSize));
                var inputBox = input.TransformToAncestor(window).TransformBounds(new Rect(input.RenderSize));
                label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                var natural = label.DesiredSize.Width;
                var gap = inputBox.Left - (labelBox.Left + Math.Min(natural, labelBox.Width));
                Assert.True(natural <= labelBox.Width + 0.5, $"{where}: the label '{label.Text}' needs {natural:0} px and gets {labelBox.Width:0} px.");
                Assert.True(gap >= MinimumGap, $"{where}: the label '{label.Text}' ends {gap:0} px before its input.");
            }

            // The first label sits clear of the left edge of the dialog content and the scroll bar clear of the right edge.
            var scrollerBox = scroller.TransformToAncestor(window).TransformBounds(new Rect(scroller.RenderSize));
            var dialogBox = dialog.TransformToAncestor(window).TransformBounds(new Rect(dialog.RenderSize));
            Assert.True(scrollerBox.Left - dialogBox.Left >= MinimumGap, $"{where}: the content starts {scrollerBox.Left - dialogBox.Left:0} px from the dialog edge.");
            Assert.True(dialogBox.Right - scrollerBox.Right >= MinimumGap, $"{where}: the content ends {dialogBox.Right - scrollerBox.Right:0} px from the dialog edge.");
        });
    }
}
