using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Models;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using TextBlock = System.Windows.Controls.TextBlock;
using Window = System.Windows.Window;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The installer drop review in a real content dialog: every field is reachable, the footer stays in the window and an empty value is not confirmed.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DropReviewDialogRenderTests
{
    /// <summary>The window the main window gives its page: the full size less the closed pane and the frame.</summary>
    private const double Frame = 48;

    /// <summary>The fields a reader must be able to read at once on the smallest window.</summary>
    private const int MinimumVisibleFields = 3;

    public static TheoryData<ApplicationTheme, int, int> Sizes => new()
    {
        { ApplicationTheme.Light, 1440, 900 },
        { ApplicationTheme.Dark, 1440, 900 },
        { ApplicationTheme.Light, 900, 600 },
        { ApplicationTheme.Dark, 900, 600 }
    };

    internal static DroppedInstallerAnalysis Analysis() => new()
    {
        Path = @"C:\Installers\Contoso-Setup-x64.exe",
        FileName = "Contoso-Setup-x64.exe",
        Engine = "InnoSetup",
        InstallerType = "EXE",
        Architecture = "x64",
        AppName = ProposedField.Confirmed("Contoso Application", "file version resource"),
        Publisher = ProposedField.Confirmed("Contoso", "file version resource"),
        SoftwareVersion = ProposedField.Confirmed("1.2.3", "file version resource"),
        InstallArgs = ProposedField.Predicted("/VERYSILENT /NORESTART", "switch table"),
        UninstallArgs = ProposedField.Predicted("/VERYSILENT", "switch table"),
        UninstallCommand = ProposedField.Confirmed("", "installer script"),
        UninstallRegistryKey = ProposedField.Predicted(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso", "switch table"),
        DetectionHint = "The key is predicted from the application name."
    };

    [Fact]
    public void AnEmptyValueIsNeverBadgedConfirmed()
    {
        var empty = ProposedFieldRow.From("Uninstall command", ProposedField.Confirmed("", "installer script"));
        Assert.NotEqual("Confirmed", empty.BadgeText);
        var filled = ProposedFieldRow.From("Uninstall command", ProposedField.Confirmed("unins000.exe /VERYSILENT", "installer script"));
        Assert.Equal("Confirmed", filled.BadgeText);
        var edited = ProposedFieldRow.From("Uninstall command", ProposedField.Confirmed("unins000.exe", "installer script"));
        edited.Value = "";
        Assert.NotEqual("Confirmed", edited.BadgeText);
    }

    private static void WithDialog(ApplicationTheme theme, int width, int height, Action<Window, ContentDialog, DropReviewPanel> test)
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
            var panel = new DropReviewPanel { FitToWindow = true };
            panel.Load(Analysis());
            var dialog = new ContentDialog
            {
                Title = "Installer drop", Content = panel, PrimaryButtonText = "Stage", SecondaryButtonText = "Save as definition", CloseButtonText = "Cancel"
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

    private static Rect Bounds(FrameworkElement element, Window window) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheReviewKeepsItsFooterInTheWindowShowsFieldsAndScrollsToTheRest(ApplicationTheme theme, int width, int height)
    {
        WithDialog(theme, width, height, (window, dialog, panel) =>
        {
            var where = $"Drop review {theme} {width}x{height}";
            var scroller = (ScrollViewer)panel.FindName("FieldsScroller");
            var list = (ItemsControl)panel.FindName("FieldList");
            var fields = Descendants(list).OfType<ContentPresenter>().Where(p => p.Content is ProposedFieldRow).ToList();
            Assert.Equal(7, fields.Count);

            // The panel's fixed rows and the dialog buttons all lie inside the window.
            foreach (var id in new[] { "DropReviewSaveApplication" })
            {
                var button = ById<Button>(panel, id);
                var bounds = Bounds(button, window);
                Assert.True(bounds.Bottom <= window.ActualHeight + 0.5, $"{where}: {id} ends at {bounds.Bottom:0} px in a {window.ActualHeight:0} px window.");
            }
            foreach (var button in Descendants(dialog).OfType<Button>().Where(b => b.IsVisible && b.Content is "Stage" or "Cancel" or "Save as definition"))
            {
                var bounds = Bounds(button, window);
                Assert.True(bounds.Bottom <= window.ActualHeight + 0.5, $"{where}: the button '{button.Content}' ends at {bounds.Bottom:0} px in a {window.ActualHeight:0} px window.");
            }

            // The fields scroll inside their own viewer, and the viewer shows at least the minimum of whole fields.
            var whole = fields.Count(f =>
            {
                var top = f.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
                return top >= -0.5 && top + f.ActualHeight <= scroller.ViewportHeight + 0.5;
            });
            Assert.True(whole >= MinimumVisibleFields, $"{where}: the review shows {whole} whole fields in a {scroller.ViewportHeight:0} px viewer of {scroller.ExtentHeight:0} px (cap {scroller.MaxHeight:0}; panel {panel.ActualHeight:0}; dialog {dialog.ActualHeight:0}; window {window.ActualHeight:0}).");
            Assert.True(scroller.ScrollableHeight + scroller.ViewportHeight >= scroller.ExtentHeight - 0.5);

            // Each field reaches the viewer through scrolling.
            foreach (var field in fields)
            {
                field.BringIntoView();
                Settle(window);
                var top = field.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
                Assert.True(top >= -0.5 && top + field.ActualHeight <= scroller.ViewportHeight + 0.5, $"{where}: a field cannot be scrolled into view.");
            }
        });
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheBadgesUseThemeBrushesAndTheEmptyUninstallCommandShowsNotConfirmed(ApplicationTheme theme, int width, int height)
    {
        WithDialog(theme, width, height, (window, dialog, panel) =>
        {
            var badges = Descendants(panel).OfType<TextBlock>().Where(t => AutomationProperties.GetAutomationId(t) == "DropReviewBadge").ToList();
            Assert.Equal(7, badges.Count);
            Assert.DoesNotContain(badges, b => b.Text == "Confirmed" && b.DataContext is ProposedFieldRow { Label: "Uninstall command" });
            var theme3 = new[] { "SystemFillColorSuccessBrush", "SystemFillColorCautionBrush" }.Select(k => ((SolidColorBrush)System.Windows.Application.Current.FindResource(k)).Color).ToList();
            foreach (var badge in badges.Where(b => b.Text is "Confirmed" or "Predicted"))
            {
                var color = ((SolidColorBrush)badge.Foreground).Color;
                Assert.Contains(color, theme3);
            }
        });
    }
}
