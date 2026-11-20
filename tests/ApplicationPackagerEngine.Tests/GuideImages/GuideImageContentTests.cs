using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests.GuideImages;

/// <summary>
/// A guide image shows content: a capture run must not replace an image with an empty-state page (the problem of the first capture run). The check is the
/// share of pixels that differ from the most common color of the image, the "ink" of the image. The same check runs in the capture script before it keeps an image.
/// </summary>
public sealed class GuideImageContentTests
{
    internal static double Ink(string path)
    {
        var decoder = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr32, null, 0);
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        // The body of the window: right of the sidebar and below the page header and the toolbars. An empty page has nothing there.
        var left = (int)(frame.PixelWidth * 0.18);
        var top = (int)(frame.PixelHeight * 0.34);
        var bottom = (int)(frame.PixelHeight * 0.95);
        var counts = new Dictionary<int, int>();
        var total = 0;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < frame.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                var color = pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16);
                counts[color] = counts.GetValueOrDefault(color) + 1;
                total++;
            }
        return 1.0 - (double)counts.Values.Max() / total;
    }

    [Fact]
    public void AnImageThatACheckRunProducesIsNotAnEmptyPageOfTheApp()
    {
        var folder = Path.Combine(CatalogServiceTests.RepositoryRoot(), "docs", "images");
        var empty = Ink(Path.Combine(CatalogServiceTests.RepositoryRoot(), "tests", "ApplicationPackagerEngine.Tests", "GuideImages", "EmptyPage.png"));
        var low = GuideCaptureSteps.Ids.Where(id => !EmptyByDesign.Contains(id)).Select(id => (Name: id, Ink: Ink(Path.Combine(folder, id + ".png")))).Where(i => i.Ink <= empty + MarginOverEmpty).OrderBy(i => i.Ink).ToList();

        Assert.True(low.Count == 0, $"These images look as empty as an empty page of the app (ink {empty:0.0000} plus {MarginOverEmpty}): " + string.Join(", ", low.Select(i => $"{i.Name} {i.Ink:0.0000}")));
    }

    /// <summary>The images that show a page with little content by design: the first step of a flow, before anything is entered, and two short Settings pages.</summary>
    public static readonly string[] EmptyByDesign = ["core-add-file-pick", "core-settings-about", "core-settings-defaults"];

    /// <summary>An image of a page with a list, a form or a result has at least this much more ink in the body than the empty page.</summary>
    public const double MarginOverEmpty = 0.01;
}