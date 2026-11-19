using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApplicationPackagerUI.Helpers;

namespace ApplicationPackagerEngine.Tests.IconPack;

/// <summary>An icon pack update replaces the icon files while the Packages grid shows them: no icon file stays open after it was read.</summary>
public sealed class IconImageLoaderTests
{
    private static void WritePng(string path, byte red)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = red; pixels[i + 3] = 255; }
        var source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte RedOf(BitmapSource image)
    {
        var px = new byte[4];
        image.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), px, 4, 0);
        return px[2];
    }

    [Fact]
    public void AnIconFileCanBeReplacedAndDeletedWhileItsImageIsShown() => StaTestHelper.Run(() =>
    {
        var folder = Directory.CreateTempSubdirectory("apd-iconload-").FullName;
        try
        {
            var path = Path.Combine(folder, "7zip.png");
            WritePng(path, 200);
            var shown = IconImageLoader.Load(path);
            Assert.NotNull(shown);

            // The update replaces the file (the pack copies over it) and a later cleanup may delete it: neither may fail.
            var replacement = Path.Combine(folder, "new.png");
            WritePng(replacement, 40);
            File.Copy(replacement, path, overwrite: true);
            var again = IconImageLoader.Load(path);
            File.Delete(path);

            Assert.Equal(200, RedOf(shown!));
            Assert.Equal(40, RedOf(again!));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    });

    [Fact]
    public void AMissingBlankOrBrokenFileGivesNoImage() => StaTestHelper.Run(() =>
    {
        var folder = Directory.CreateTempSubdirectory("apd-iconload-").FullName;
        try
        {
            var broken = Path.Combine(folder, "broken.png");
            File.WriteAllText(broken, "not a picture");
            Assert.Null(IconImageLoader.Load(null));
            Assert.Null(IconImageLoader.Load(" "));
            Assert.Null(IconImageLoader.Load(Path.Combine(folder, "none.png")));
            Assert.Null(IconImageLoader.Load(broken));
        }
        finally { Directory.Delete(folder, recursive: true); }
    });
}
