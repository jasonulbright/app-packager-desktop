using System.Drawing;
using System.Drawing.Imaging;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class IconExtractionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-icon-" + Guid.NewGuid().ToString("N"));

    public IconExtractionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Builds an RT_GROUP_ICON directory: a 6-byte ICONDIR followed by one
    /// 14-byte GRPICONDIRENTRY per frame.
    /// </summary>
    private static byte[] GroupDirectory(params (int Width, int Height, int BitCount, int Id)[] entries)
    {
        var bytes = new byte[6 + (entries.Length * 14)];
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 2);
        BitConverter.GetBytes((ushort)entries.Length).CopyTo(bytes, 4);
        for (var i = 0; i < entries.Length; i++)
        {
            var off = 6 + (i * 14);
            bytes[off] = (byte)(entries[i].Width == 256 ? 0 : entries[i].Width);
            bytes[off + 1] = (byte)(entries[i].Height == 256 ? 0 : entries[i].Height);
            BitConverter.GetBytes((ushort)1).CopyTo(bytes, off + 4);
            BitConverter.GetBytes((ushort)entries[i].BitCount).CopyTo(bytes, off + 6);
            BitConverter.GetBytes(64).CopyTo(bytes, off + 8);
            BitConverter.GetBytes((ushort)entries[i].Id).CopyTo(bytes, off + 12);
        }
        return bytes;
    }

    private static byte[] PngBytes(int size, bool noisy = false)
    {
        using var bitmap = new Bitmap(size, size);
        var random = new Random(7);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                bitmap.SetPixel(x, y, noisy
                    ? Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))
                    : Color.FromArgb(255, 10, 120, 200));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>Wraps a PNG frame in a single-entry .ico container.</summary>
    private static byte[] PngIco(int size, bool noisy = false)
    {
        var frame = PngBytes(size, noisy);
        var ico = new byte[22 + frame.Length];
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 2);
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 4);
        ico[6] = (byte)(size == 256 ? 0 : size);
        ico[7] = (byte)(size == 256 ? 0 : size);
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 10);
        BitConverter.GetBytes((ushort)32).CopyTo(ico, 12);
        BitConverter.GetBytes(frame.Length).CopyTo(ico, 14);
        BitConverter.GetBytes(22).CopyTo(ico, 18);
        frame.CopyTo(ico, 22);
        return ico;
    }

    // ── group-icon directory parsing ─────────────────────────────

    [Fact]
    public void BuildIcoFromGroup_SelectsTheLargestFrame()
    {
        var group = GroupDirectory((16, 16, 32, 1), (48, 48, 32, 2), (32, 32, 32, 3));
        var image = new byte[] { 1, 2, 3, 4 };

        var ico = IconExtractionService.BuildIcoFromGroup(group, id => id == 2 ? image : null, out var size);

        Assert.NotNull(ico);
        Assert.Equal(48, size);
        Assert.Equal(22 + image.Length, ico!.Length);
        Assert.Equal(48, ico[6]);
        Assert.Equal(image.Length, BitConverter.ToInt32(ico, 14));
        Assert.Equal(22, BitConverter.ToInt32(ico, 18));
        Assert.Equal(image, ico[22..]);
    }

    [Fact]
    public void BuildIcoFromGroup_ReadsAZeroDimensionByteAs256()
    {
        var group = GroupDirectory((128, 128, 32, 1), (256, 256, 32, 2));

        var ico = IconExtractionService.BuildIcoFromGroup(group, _ => [9], out var size);

        Assert.NotNull(ico);
        Assert.Equal(256, size);
        // The 256 entry keeps its zero-byte encoding in the emitted directory.
        Assert.Equal(0, ico![6]);
    }

    [Fact]
    public void BuildIcoFromGroup_BreaksASizeTieOnColourDepth()
    {
        var group = GroupDirectory((64, 64, 8, 1), (64, 64, 32, 2));

        var ico = IconExtractionService.BuildIcoFromGroup(group, id => id == 2 ? [7] : [8], out _);

        Assert.Equal(new byte[] { 7 }, ico![22..]);
    }

    [Fact]
    public void BuildIcoFromGroup_RejectsATruncatedDirectory()
    {
        var group = GroupDirectory((32, 32, 32, 1))[..10];

        Assert.Null(IconExtractionService.BuildIcoFromGroup(group, _ => [1], out var size));
        Assert.Equal(0, size);
    }

    [Fact]
    public void BuildIcoFromGroup_ReturnsNullWhenTheChosenImageIsMissing()
    {
        var group = GroupDirectory((64, 64, 32, 5));

        Assert.Null(IconExtractionService.BuildIcoFromGroup(group, _ => null, out _));
    }

    [Fact]
    public void GetIconBytesDimension_ReadsTheLargestFrameOfAnIcoFile()
    {
        Assert.Equal(256, IconExtractionService.GetIconBytesDimension(PngIco(256)));
        Assert.Equal(64, IconExtractionService.GetIconBytesDimension(PngIco(64)));
        Assert.Equal(0, IconExtractionService.GetIconBytesDimension([1, 2, 3]));
        Assert.Equal(0, IconExtractionService.GetIconBytesDimension(PngBytes(64)));
    }

    // ── size floor and the 256 KB cap ────────────────────────────

    [Fact]
    public void MaterializeIcon_RejectsAnImageUnderTheFloor()
    {
        var logs = new List<string>();
        var result = IconExtractionService.MaterializeIcon(
            PngIco(16), 16, "PeResource", Path.Combine(_dir, "app-icon.ico"),
            IconExtractionService.MinimumDimension, "setup.exe", logs.Add);

        Assert.Null(result);
        Assert.False(File.Exists(Path.Combine(_dir, "app-icon.ico")));
        Assert.Contains(logs, l => l.Contains("too small") && l.Contains("16px"));
    }

    [Fact]
    public void MaterializeIcon_AcceptsAnImageAtTheFloor()
    {
        var result = IconExtractionService.MaterializeIcon(
            PngIco(32), 32, "PeResource", Path.Combine(_dir, "app-icon.ico"));

        Assert.NotNull(result);
        Assert.Equal(32, result!.PixelSize);
        Assert.Equal("ico", result.Format);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public void MaterializeIcon_CopiesAPngCompressedFrameOutByteForByte()
    {
        var frame = PngBytes(256);
        var ico = new byte[22 + frame.Length];
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 2);
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 4);
        BitConverter.GetBytes(frame.Length).CopyTo(ico, 14);
        BitConverter.GetBytes(22).CopyTo(ico, 18);
        frame.CopyTo(ico, 22);

        var result = IconExtractionService.MaterializeIcon(
            ico, 256, "PeResource", Path.Combine(_dir, "app-icon.png"));

        Assert.NotNull(result);
        Assert.Equal("png", result!.Format);
        Assert.Equal(frame, File.ReadAllBytes(result.Path));
    }

    [Fact]
    public void MaterializeIcon_DownscalesAnImageOverTheMecmCeiling()
    {
        var result = IconExtractionService.MaterializeIcon(
            PngIco(1024), 1024, "PeResource", Path.Combine(_dir, "app-icon.ico"));

        Assert.NotNull(result);
        Assert.Equal(IconExtractionService.MaxDimension, result!.PixelSize);
        Assert.Equal("png", result.Format);
        using var image = Image.FromFile(result.Path);
        Assert.Equal(IconExtractionService.MaxDimension, image.Width);
    }

    [Fact]
    public void MaterializeIcon_ReEncodesAnOversizedIconUnderTheCap()
    {
        // Noise defeats PNG prediction, so a 512px frame lands over 256 KB and
        // has to downscale before it fits.
        var ico = PngIco(512, noisy: true);
        Assert.True(ico.Length > IconExtractionService.MaxIconBytes);

        var result = IconExtractionService.MaterializeIcon(
            ico, 512, "PeResource", Path.Combine(_dir, "app-icon.ico"));

        Assert.NotNull(result);
        Assert.Equal("png", result!.Format);
        Assert.True(new FileInfo(result.Path).Length <= IconExtractionService.MaxIconBytes);
        Assert.True(result.PixelSize < 512);
        Assert.False(File.Exists(Path.Combine(_dir, "app-icon.ico")));
    }

    // ── IconSource verdicts and staging ──────────────────────────

    [Theory]
    [InlineData("Installer", "Installer")]
    [InlineData("External", "External")]
    [InlineData("None", "None")]
    [InlineData("", "None")]
    [InlineData(null, "None")]
    [InlineData("installer", "None")]
    [InlineData("nonsense", "None")]
    public void NormalizeIconSource_KeepsAnythingUnrecognizedOnThePreIconPath(string? value, string expected) =>
        Assert.Equal(expected, IconExtractionService.NormalizeIconSource(value));

    private AppDefinition Definition(string iconSource)
    {
        var definitionPath = Path.Combine(_dir, "Definitions", "widget.json");
        Directory.CreateDirectory(Path.GetDirectoryName(definitionPath)!);
        File.WriteAllText(definitionPath, "{}");
        return new AppDefinition { Id = "abc123", IconSource = iconSource, SourcePath = definitionPath };
    }

    [Fact]
    public void StageIcon_None_StagesNothing()
    {
        var stage = Path.Combine(_dir, "stage");
        Directory.CreateDirectory(stage);
        var manifest = new StageManifest { InstallerFile = "setup.exe" };

        IconExtractionService.StageIcon(Definition("None"), stage, manifest);

        Assert.Null(manifest.Icon);
        Assert.Empty(Directory.GetFiles(stage));
    }

    [Fact]
    public void StageIcon_External_CopiesTheDefinitionNamedIconIntoTheStage()
    {
        var definition = Definition("External");
        var icons = IconExtractionService.ExternalIconsDirectory(definition);
        Directory.CreateDirectory(icons);
        var iconBytes = PngBytes(64);
        File.WriteAllBytes(Path.Combine(icons, "widget.png"), iconBytes);

        var stage = Path.Combine(_dir, "stage-external");
        Directory.CreateDirectory(stage);
        var manifest = new StageManifest();

        IconExtractionService.StageIcon(definition, stage, manifest);

        Assert.Equal("app-icon.png", manifest.Icon);
        Assert.Equal(iconBytes, File.ReadAllBytes(Path.Combine(stage, "app-icon.png")));
    }

    [Fact]
    public void StageIcon_External_WarnsAndContinuesWhenNoIconFileExists()
    {
        var stage = Path.Combine(_dir, "stage-missing");
        Directory.CreateDirectory(stage);
        var manifest = new StageManifest();
        var logs = new List<string>();

        IconExtractionService.StageIcon(Definition("External"), stage, manifest, logs.Add);

        Assert.Null(manifest.Icon);
        Assert.Contains(logs, l => l.Contains("widget.ico|png"));
    }

    [Fact]
    public void StageIcon_Installer_WarnsWhenThePayloadIsNotInTheStage()
    {
        var stage = Path.Combine(_dir, "stage-nopayload");
        Directory.CreateDirectory(stage);
        var manifest = new StageManifest { InstallerFile = "setup.exe" };
        var logs = new List<string>();

        IconExtractionService.StageIcon(Definition("Installer"), stage, manifest, logs.Add);

        Assert.Null(manifest.Icon);
        Assert.Contains(logs, l => l.Contains("not in the stage folder"));
    }

    [Fact]
    public void StagedIcon_IsCoveredByTheManifestFileHashes()
    {
        var definition = Definition("External");
        var icons = IconExtractionService.ExternalIconsDirectory(definition);
        Directory.CreateDirectory(icons);
        File.WriteAllBytes(Path.Combine(icons, "widget.png"), PngBytes(64));

        var stage = Path.Combine(_dir, "stage-hashed");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "install.bat"), "@echo off");
        var manifest = new StageManifest { AppName = "Widget", InstallerFile = "widget.msi" };

        IconExtractionService.StageIcon(definition, stage, manifest);
        new ManifestService().Write(Path.Combine(stage, "stage-manifest.json"), manifest);

        var entry = Assert.Single(manifest.FileHashes!, h => h.RelativePath == "app-icon.png");
        Assert.Equal(new FileInfo(Path.Combine(stage, "app-icon.png")).Length, entry.Size);
        Assert.Equal(64, entry.Sha256.Length);

        var round = new ManifestService().Read(Path.Combine(stage, "stage-manifest.json"));
        Assert.Equal("app-icon.png", round.Icon);
    }

    // ── live extraction against a real PE ────────────────────────

    [Fact]
    public void ExtractInstallerIcon_ReadsTheTrueResourceSizeFromARealModule()
    {
        var module = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(module)) return;

        var result = IconExtractionService.ExtractInstallerIcon(module, Path.Combine(_dir, "live.ico"));

        Assert.NotNull(result);
        // ExtractAssociatedIcon caps at 32; the resource directory reports the
        // size the module actually carries.
        Assert.Equal("PeResource", result!.Source);
        Assert.True(result.PixelSize > 32, $"expected a true-size frame, got {result.PixelSize}px");
        Assert.True(File.Exists(result.Path));
        Assert.True(new FileInfo(result.Path).Length <= IconExtractionService.MaxIconBytes);
    }
}
