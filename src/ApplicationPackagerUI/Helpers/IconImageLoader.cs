using System.IO;
using System.Windows.Media.Imaging;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Loads an icon file fully into memory and closes it. The Packages grid shows the icon pack, and an icon pack update replaces
/// those files while the grid is open; an image that keeps its file open (the default of an image source address) makes the
/// update fail with "file in use". A stream source is never in the image cache (a cache key would need an address), so a replaced file shows its new picture.
/// </summary>
public static class IconImageLoader
{
    public static BitmapImage? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
