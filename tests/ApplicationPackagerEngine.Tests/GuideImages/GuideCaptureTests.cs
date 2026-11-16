using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests.GuideImages;

/// <summary>
/// The capture run of the guide images (decision D-21). It renders each page of <see cref="GuideCaptureSteps"/> at the work area of a maximized
/// window on a 1080p display in both themes and saves the images as <c>light\id.png</c> and <c>dark\id.png</c> under the folder that the
/// variable APD_GUIDE_CAPTURE_DIR names. Without the variable the test does nothing, so an ordinary test run writes no file.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class GuideCaptureTests
{
    public const string FolderVariable = "APD_GUIDE_CAPTURE_DIR";

    public static TheoryData<ApplicationTheme> Themes => new() { ApplicationTheme.Light, ApplicationTheme.Dark };

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheCapturedPagesAreSavedByGuideImageId(ApplicationTheme theme)
    {
        var folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (string.IsNullOrWhiteSpace(folder)) return;
        var target = Path.Combine(folder, theme.ToString().ToLowerInvariant());
        Directory.CreateDirectory(target);

        using var pages = new PageHeaderRenderTests();
        foreach (var group in GuideCaptureSteps.PageOfImage.GroupBy(p => p.Value, p => p.Key))
        {
            var name = group.Key;
            PageHeaderRenderTests.With(name, theme, () =>
            {
                var (page, owner) = pages.Create(name);
                var window = new Window
                {
                    Width = GuideCaptureSteps.WorkAreaWidth - GuideCaptureSteps.NavigationPaneWidth,
                    Height = GuideCaptureSteps.WorkAreaHeight - GuideCaptureSteps.FrameHeight,
                    WindowStyle = WindowStyle.None,
                    Content = page
                };
                Show(window);
                try
                {
                    Settle(window);
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    // The theme paints its background on the window, which is not rendered: the image gets it first.
                    var backdrop = new DrawingVisual();
                    using (var dc = backdrop.RenderOpen())
                        dc.DrawRectangle((System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Render(backdrop);
                    bitmap.Render(content);
                    foreach (var id in group)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(target, id + ".png"));
                        encoder.Save(stream);
                    }
                }
                finally
                {
                    window.Close();
                    owner?.Dispose();
                }
            });
        }
    }
}
