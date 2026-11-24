using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Border = System.Windows.Controls.Border;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using StackPanel = System.Windows.Controls.StackPanel;
using UiButton = Wpf.Ui.Controls.Button;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-125: the glyph of a button and its text sit on one line, wherever the button sits. The text of a control snaps to the pixel grid on its own, so a control at a fractional
/// position put its glyph and its text on different pixel rows: the icon looked a pixel high on some pages and not on others. The app rounds the layout of every window and page
/// (LayoutRounding). The button is drawn with the app styles at 100, 125 and 150 percent of display scale and at several fractional positions, and the pixels give the offset
/// between the centre of the glyph and the centre of the first capital letter of the text.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ButtonAlignmentTests
{
    private static readonly string[] Labels = ["Cancel", "Check Latest", "Stage", "One Click"];
    private static readonly double[] Tops = [0.0, 0.25, 0.5, 0.75];

    /// <summary>One device pixel is the resolution of the measure; the live probe (Probe-CancelAlignment) holds the glyph to half a pixel at 100 percent.</summary>
    internal const double MaxOffsetDevice = 1.0;

    internal sealed record Measure(string Label, double Scale, VerticalAlignment Align, double Top, double OffsetDevice, string Boxes);

    private static (int Y0, int Y1) Rows(List<int>[] rowsOfColumn, int from, int to)
    {
        var ys = new List<int>();
        for (var x = from; x <= to; x++) ys.AddRange(rowsOfColumn[x]);
        return (ys.Min(), ys.Max());
    }

    internal static Measure Draw(ApplicationTheme theme, double scale, string label, ControlAppearance appearance, double top, VerticalAlignment align)
    {
        Measure? result = null;
        RenderKit.WithApplication(theme, () =>
        {
            var button = new UiButton { Content = label, Icon = new SymbolIcon(SymbolRegular.Dismiss24), Appearance = appearance, Margin = new Thickness(8, 8 + top, 8, 8), VerticalAlignment = align };
            var root = new Border { Child = new Grid { Height = 63 + top, Children = { button } } };
            root.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
            VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
            // The window of the app rounds its layout (LayoutRounding); the root of the picture does the same.
            root.UseLayoutRounding = true;
            root.Measure(new Size(260, 80));
            root.Arrange(new Rect(0, 0, 260, 80));
            root.UpdateLayout();
            var width = (int)Math.Ceiling(root.ActualWidth * scale);
            var height = (int)Math.Ceiling(root.ActualHeight * scale);
            var target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            target.Render(root);
            var pixels = new byte[width * height * 4];
            target.CopyPixels(pixels, width * 4, 0);

            // The rectangle of the button inside the picture, in device pixels.
            var origin = button.TranslatePoint(new Point(0, 0), root);
            var bx0 = (int)Math.Round(origin.X * scale);
            var by0 = (int)Math.Round(origin.Y * scale);
            var bw = (int)Math.Round(button.ActualWidth * scale);
            var bh = (int)Math.Round(button.ActualHeight * scale);
            var inset = (int)Math.Ceiling(5 * scale);

            Color At(int x, int y)
            {
                var i = ((by0 + y) * width + bx0 + x) * 4;
                return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
            }

            var background = At(inset + 2, inset);
            int Distance(Color c) => Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);
            var strongest = 0;
            for (var x = inset; x < bw - inset; x++)
                for (var y = inset; y < bh - inset; y++)
                    strongest = Math.Max(strongest, Distance(At(x, y)));
            var limit = strongest / 2;

            var rowsOfColumn = Enumerable.Range(0, bw).Select(_ => new List<int>()).ToArray();
            for (var x = inset; x < bw - inset; x++)
                for (var y = inset; y < bh - inset; y++)
                    if (Distance(At(x, y)) > limit) rowsOfColumn[x].Add(y);

            // The glyph is the first group of columns with ink; a gap of 6 logical pixels or more ends a group. The text starts with the next group, and its
            // first capital letter is the columns up to the first column without ink.
            var gapMin = (int)Math.Round(6 * scale);
            var groups = new List<(int From, int To)>();
            int start = -1, last = -1;
            for (var x = inset; x < bw - inset; x++)
            {
                if (rowsOfColumn[x].Count == 0) continue;
                if (start < 0) start = x;
                else if (x - last >= gapMin) { groups.Add((start, last)); start = x; }
                last = x;
            }

            if (start >= 0) groups.Add((start, last));
            Assert.True(groups.Count >= 2, $"{label}: the button shows {groups.Count} group of ink (glyph and text expected).");
            var icon = Rows(rowsOfColumn, groups[0].From, groups[0].To);
            var letterEnd = groups[1].From;
            while (letterEnd + 1 <= groups[1].To && rowsOfColumn[letterEnd + 1].Count > 0) letterEnd++;
            var capital = Rows(rowsOfColumn, groups[1].From, letterEnd);
            var offsetDevice = (capital.Y0 + capital.Y1) / 2.0 - (icon.Y0 + icon.Y1) / 2.0;
            result = new Measure(label, scale, align, top, offsetDevice, $"glyph y {icon.Y0}-{icon.Y1}, capital y {capital.Y0}-{capital.Y1}, button {bw}x{bh}");
        });
        return result!;
    }

    public static TheoryData<string, double, string> Cases()
    {
        var data = new TheoryData<string, double, string>();
        foreach (var theme in new[] { "Light", "Dark" })
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
                foreach (var appearance in new[] { "Secondary", "Danger" })
                    data.Add(theme, scale, appearance);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheGlyphAndTheTextOfAButtonShareOneLineWhereverTheButtonSits(string theme, double scale, string appearance)
    {
        var results = new List<Measure>();
        foreach (var label in Labels)
            foreach (var top in Tops)
                foreach (var align in new[] { VerticalAlignment.Top, VerticalAlignment.Center })
                    results.Add(Draw(Enum.Parse<ApplicationTheme>(theme), scale, label, Enum.Parse<ControlAppearance>(appearance), top, align));

        var report = string.Join(Environment.NewLine, results.Select(r => $"{r.Label} {r.Scale:0.00} top {r.Top:0.00} {r.Align}: offset {r.OffsetDevice:0.0} device px; {r.Boxes}"));
        Assert.True(results.All(r => Math.Abs(r.OffsetDevice) <= MaxOffsetDevice), "The glyph and the text are more than " + MaxOffsetDevice + " device px apart:" + Environment.NewLine + report);
        foreach (var label in Labels)
        {
            var offsets = results.Where(r => r.Label == label).Select(r => r.OffsetDevice).Distinct().ToList();
            Assert.True(offsets.Count == 1, "The offset between glyph and text of '" + label + "' depends on the position of the button:" + Environment.NewLine + report);
        }
    }

    [Fact]
    public void TheButtonStyleRoundsItsLayoutToDevicePixels()
    {
        var path = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Styles", "ButtonText.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var style = XDocument.Load(path).Descendants(presentation + "Style").Single(s => (string?)s.Attribute("TargetType") == "ui:Button");

        Assert.Equal("True", (string?)style.Elements(presentation + "Setter").Single(s => (string?)s.Attribute("Property") == "UseLayoutRounding")!.Attribute("Value"));
    }

    [Fact]
    public void EveryWindowAndEveryPageRoundsItsLayoutToDevicePixels()
    {
        // The value of the window does not reach the page that a frame hosts (the pages sat on fractional positions), so the page gets the value as well.
        RenderKit.WithApplication(ApplicationTheme.Light, () =>
        {
            ApplicationPackagerUI.Helpers.LayoutRounding.Register();
            var panel = new StackPanel();
            var page = new Page { Content = panel };
            var host = new Frame { Content = page };
            var window = new Window { Content = host, UseLayoutRounding = false };
            try
            {
                RenderKit.Show(window);
                Assert.True(window.UseLayoutRounding, "The window does not round its layout.");
                Assert.True(page.UseLayoutRounding, "The page does not round its layout.");
                Assert.True(panel.UseLayoutRounding, "A control on the page does not round its layout.");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
